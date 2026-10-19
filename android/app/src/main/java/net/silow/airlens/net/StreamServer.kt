package net.silow.airlens.net

import net.silow.airlens.util.AirLog
import java.io.IOException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * TCP server for one PC at a time.
 *
 * - Binds `0.0.0.0:port`, so one listener serves Wi-Fi clients and `adb forward` (which arrives on
 *   loopback). `SO_REUSEADDR` lets a restarted service rebind while old sockets sit in TIME_WAIT.
 * - **A newly authenticated client preempts the current one.** A PC app that crashed and restarted
 *   never waits for a half-dead socket to time out.
 * - It caches the latest codec config and replays it to every new session. That makes
 *   `KEY_PREPEND_HEADER_TO_SYNC_FRAMES` an optimization rather than a requirement, which matters on
 *   OMX encoders (e.g. HiSilicon) that do not support it.
 * - Keyframe requests from any source are rate-limited, because an IDR storm is itself congestion.
 *
 * [submit] is called on the encoder thread and never blocks.
 */
class StreamServer(
    private val port: Int,
    private val pool: EncodedFramePool,
    private val listener: Listener,
    private val clientTimeoutMs: Int = 3000,
    private val handshakeTimeoutMs: Int = 2000,
) {
    interface Listener {
        fun onClientConnected(remoteAddress: String)
        fun onClientDisconnected(remoteAddress: String, reason: String)
        /** Already rate-limited; forward straight to the encoder. */
        fun onKeyframeNeeded()
        fun onCongestion()
        fun onReceiverReport(report: ReceiverReport)
        /** The active PC asked for a lens or quality change. Answer with [updateDeviceState]. */
        fun onControl(request: ControlRequest) = Unit
    }

    private val sessionLock = Any()
    private var serverSocket: ServerSocket? = null
    private var acceptThread: Thread? = null
    @Volatile private var running = false
    @Volatile private var session: ClientSession? = null
    private var streamInfo: StreamInfo? = null // guarded by sessionLock
    private var cachedConfig: ByteArray? = null // guarded by sessionLock
    private var deviceState: ByteArray? = null // guarded by sessionLock
    private var cachedConfigPtsUs = 0L
    private val sessionIds = AtomicInteger()
    private val lastKeyframeRequestNanos = AtomicLong(0L)

    /** Queue budgets; updated with the bitrate so the latency cap stays ~100 ms. */
    @Volatile private var maxQueuedBytes = MIN_QUEUE_BYTES
    @Volatile private var sendBufferBytes = MIN_SEND_BUFFER

    val boundPort: Int get() = serverSocket?.localPort ?: -1
    val activeSession: ClientSession? get() = session

    @Throws(IOException::class)
    fun start() {
        check(!running) { "already started" }
        val ss = ServerSocket()
        ss.reuseAddress = true
        ss.bind(InetSocketAddress(port))
        serverSocket = ss
        running = true
        acceptThread = Thread(::acceptLoop, "AirLens-Accept").also { it.start() }
        AirLog.i(TAG, "listening on port ${ss.localPort}")
    }

    fun stop() {
        running = false
        try {
            serverSocket?.close()
        } catch (_: IOException) {
        }
        acceptThread?.join(1000)
        acceptThread = null
        serverSocket = null
        val s = synchronized(sessionLock) { session.also { session = null } }
        s?.close("server stopping")
        s?.join(1000)
        synchronized(sessionLock) { cachedConfig = null }
    }

    /**
     * Sizes the send queue and socket buffer to the stream's bitrate: ~100 ms of data each.
     * A small kernel buffer keeps the backlog visible to [SendQueue], where it can be dropped
     * intelligently, instead of hiding in the kernel where it can only add latency.
     */
    fun configureForBitrate(bitrateBps: Int) {
        val per100ms = bitrateBps / 8 / 10
        maxQueuedBytes = per100ms.coerceIn(MIN_QUEUE_BYTES, MAX_QUEUE_BYTES)
        sendBufferBytes = per100ms.coerceIn(MIN_SEND_BUFFER, MAX_SEND_BUFFER)
        session?.queue?.maxBytes = maxQueuedBytes
    }

    /** New encoder epoch or changed stream parameters. Clears the cached config on epoch change. */
    fun updateStreamInfo(info: StreamInfo) {
        synchronized(sessionLock) {
            if (streamInfo?.epoch != info.epoch) cachedConfig = null
            streamInfo = info
            session?.setStreamInfo(info)
        }
    }

    /** Lenses and quality presets the PC may choose from; replayed to every new session. */
    fun updateDeviceState(json: ByteArray) {
        synchronized(sessionLock) {
            deviceState = json
            session?.setDeviceState(json)
        }
    }

    /** Takes ownership of [frame]. Called on the encoder thread. */
    fun submit(frame: EncodedFrame) {
        if (frame.isConfig) {
            synchronized(sessionLock) {
                cachedConfig = frame.data.copyOfRange(Protocol.HEADER_SIZE, Protocol.HEADER_SIZE + frame.size)
                cachedConfigPtsUs = frame.ptsUs
                val s = session
                if (s == null) frame.recycle() else s.queue.offer(frame)
            }
            return
        }
        val s = session
        if (s == null) frame.recycle() else s.queue.offer(frame)
    }

    fun requestKeyframe() {
        val now = System.nanoTime()
        val last = lastKeyframeRequestNanos.get()
        if (now - last < MIN_KEYFRAME_INTERVAL_NANOS) return
        if (lastKeyframeRequestNanos.compareAndSet(last, now)) listener.onKeyframeNeeded()
    }

    private fun acceptLoop() {
        while (running) {
            val socket = try {
                serverSocket?.accept() ?: break
            } catch (e: IOException) {
                if (!running) break
                AirLog.w(TAG, "accept failed: ${e.message}")
                Thread.sleep(100)
                continue
            }
            try {
                handshake(socket)
                install(socket)
            } catch (e: IOException) {
                AirLog.w(TAG, "rejected ${socket.remoteSocketAddress}: ${e.message}")
                try {
                    socket.close()
                } catch (_: IOException) {
                }
            }
        }
    }

    private fun handshake(socket: Socket) {
        socket.tcpNoDelay = true
        socket.sendBufferSize = sendBufferBytes
        socket.soTimeout = handshakeTimeoutMs
        // DSCP AF41 -> Wi-Fi WMM "video" access category. Best effort; some kernels refuse it.
        try {
            socket.trafficClass = 0x88
        } catch (_: SocketException) {
        }
        val buf = ByteArray(Protocol.HEADER_SIZE + Protocol.HELLO_SIZE)
        val input = socket.getInputStream()
        ClientSession.readFully(input, buf, Protocol.HEADER_SIZE)
        val header = PacketHeader().parse(buf, 0, Protocol.MAX_CLIENT_PAYLOAD)
        if (header.type != Protocol.TYPE_HELLO || header.payloadLength < Protocol.HELLO_SIZE) {
            throw ProtocolException("expected HELLO, got type 0x${header.type.toString(16)}")
        }
        ClientSession.readFully(input, buf, header.payloadLength.coerceAtMost(buf.size))
        val magic = Protocol.getInt(buf, 0)
        val version = Protocol.getUShort(buf, 4)
        if (magic != Protocol.MAGIC) throw ProtocolException("bad magic 0x${magic.toString(16)}")
        if (version != Protocol.VERSION) throw ProtocolException("unsupported protocol version $version")
    }

    private fun install(socket: Socket) {
        val previous: ClientSession?
        val next: ClientSession
        synchronized(sessionLock) {
            // A handshake can finish after stop() closed the listener; never resurrect a session then.
            if (!running) throw IOException("server stopped during handshake")
            previous = session
            next = ClientSession(
                socket = socket,
                id = sessionIds.incrementAndGet(),
                maxQueuedFrames = MAX_QUEUED_FRAMES,
                maxQueuedBytes = maxQueuedBytes,
                clientTimeoutMs = clientTimeoutMs,
                listener = sessionListener,
            )
            session = next
            next.start(streamInfo)
            deviceState?.let { next.setDeviceState(it) }
            // Replay the parameter sets so the PC can decode the very next keyframe.
            cachedConfig?.let { config ->
                val frame = pool.acquire()
                frame.append(config, 0, config.size)
                frame.flags = Protocol.FLAG_CODEC_CONFIG
                frame.ptsUs = cachedConfigPtsUs
                next.queue.offer(frame)
            }
        }
        previous?.close("replaced by ${next.remoteAddress}")
        AirLog.i(TAG, "session ${next.id} connected from ${next.remoteAddress}")
        // Ask for the IDR first, bypassing the rate limit: a new viewer must not wait up to 250 ms
        // for its first picture. Only then announce the client, so whoever reacts to the
        // connection (the UI, the tests) can rely on the request having been made.
        lastKeyframeRequestNanos.set(System.nanoTime())
        listener.onKeyframeNeeded()
        listener.onClientConnected(next.remoteAddress)
    }

    private val sessionListener = object : ClientSession.Listener {
        override fun onSessionKeyframeRequest(session: ClientSession) {
            if (session === this@StreamServer.session) requestKeyframe()
        }

        override fun onSessionOverflow(session: ClientSession) {
            if (session !== this@StreamServer.session) return
            requestKeyframe()
            listener.onCongestion()
        }

        override fun onSessionReport(session: ClientSession, report: ReceiverReport) {
            if (session === this@StreamServer.session) listener.onReceiverReport(report)
        }

        override fun onSessionControl(session: ClientSession, request: ControlRequest) {
            if (session === this@StreamServer.session) listener.onControl(request)
        }

        override fun onSessionClosed(session: ClientSession, reason: String) {
            val wasActive = synchronized(sessionLock) {
                (this@StreamServer.session === session).also { if (it) this@StreamServer.session = null }
            }
            // A preempted session's close is not a disconnect from the user's point of view.
            if (wasActive) listener.onClientDisconnected(session.remoteAddress, reason)
        }
    }

    companion object {
        private const val TAG = "AirLens.Server"
        const val MAX_QUEUED_FRAMES = 8
        const val MIN_QUEUE_BYTES = 192 * 1024
        const val MAX_QUEUE_BYTES = 4 * 1024 * 1024
        const val MIN_SEND_BUFFER = 128 * 1024
        const val MAX_SEND_BUFFER = 2 * 1024 * 1024
        private const val MIN_KEYFRAME_INTERVAL_NANOS = 250_000_000L
    }
}
