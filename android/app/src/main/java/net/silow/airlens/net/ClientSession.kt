package net.silow.airlens.net

import android.os.Process
import net.silow.airlens.util.AirLog
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

/**
 * One connected PC. Two threads per session:
 *
 * - **Writer**: drains [queue] and writes each frame with a single `write()` (header in place).
 *   It is the only thread that writes to the socket, so packets are never interleaved.
 * - **Reader**: handles PING / REQUEST_KEYFRAME / RECEIVER_REPORT / CONTROL. Its `SO_TIMEOUT` is the liveness
 *   watchdog: a PC that goes silent for [clientTimeoutMs] gets its socket closed, which also unblocks
 *   a writer stuck in `write()` on a dead Wi-Fi link (Java offers no write timeout).
 *
 * Every failure funnels into [close], which is idempotent and never throws. A broken session never
 * affects the encoder or the server; the frames it held go back to the pool.
 */
class ClientSession(
    private val socket: Socket,
    val id: Int,
    maxQueuedFrames: Int,
    maxQueuedBytes: Int,
    private val clientTimeoutMs: Int,
    private val listener: Listener,
) {
    interface Listener {
        fun onSessionKeyframeRequest(session: ClientSession)
        fun onSessionOverflow(session: ClientSession)
        fun onSessionReport(session: ClientSession, report: ReceiverReport)
        fun onSessionControl(session: ClientSession, request: ControlRequest) = Unit
        fun onSessionClosed(session: ClientSession, reason: String)
    }

    val remoteAddress: String = socket.remoteSocketAddress?.toString()?.removePrefix("/") ?: "unknown"

    val queue = SendQueue(maxQueuedFrames, maxQueuedBytes) { listener.onSessionOverflow(this) }

    private val closed = AtomicBoolean(false)
    private val pendingInfo = AtomicReference<StreamInfo?>(null)
    private val pendingState = AtomicReference<ByteArray?>(null)
    private var currentEpoch = -1

    // PING is answered by the writer, never the reader: a reader blocked in write() could no
    // longer notice that the client went silent. Packed as one object so echo/receive stay paired.
    private class PendingPong(val echoUs: Long, val receivedUs: Long)
    private val pendingPong = AtomicReference<PendingPong?>(null)

    private val controlBuf = ByteArray(Protocol.HEADER_SIZE + maxOf(Protocol.STREAM_INFO_SIZE, Protocol.PONG_SIZE))
    private var frameSequence = 0
    private var lastWriteNanos = System.nanoTime()

    @Volatile var framesSent = 0L
        private set

    @Volatile var bytesSent = 0L
        private set

    private val writer = Thread(::writeLoop, "AirLens-Tx-$id")
    private val reader = Thread(::readLoop, "AirLens-Rx-$id")

    /** [initialInfo] is sent before anything else; frames start at the next keyframe. */
    fun start(initialInfo: StreamInfo?) {
        if (initialInfo != null) setStreamInfo(initialInfo)
        writer.start()
        reader.start()
    }

    /**
     * Queues a STREAM_INFO. A new encoder epoch also drops queued frames: they belong to a stream
     * the receiver is about to flush. A same-epoch update (e.g. rotation in direct mode) keeps them.
     */
    fun setStreamInfo(info: StreamInfo) {
        if (info.epoch != currentEpoch) {
            currentEpoch = info.epoch
            queue.reset()
        }
        pendingInfo.set(info)
        queue.kick()
    }

    /** Queues a DEVICE_STATE (JSON). Only the newest one is sent; it always follows STREAM_INFO. */
    fun setDeviceState(json: ByteArray) {
        pendingState.set(json)
        queue.kick()
    }

    val isClosed: Boolean get() = closed.get()

    fun close(reason: String) {
        if (!closed.compareAndSet(false, true)) return
        queue.close()
        try {
            socket.close()
        } catch (_: IOException) {
        }
        AirLog.i(TAG, "session $id ($remoteAddress) closed: $reason")
        listener.onSessionClosed(this, reason)
    }

    /** Waits briefly for both threads to exit; only used during server shutdown. */
    fun join(timeoutMs: Long) {
        writer.join(timeoutMs)
        reader.join(timeoutMs)
    }

    private fun writeLoop() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY)
        val out = try {
            socket.getOutputStream()
        } catch (e: IOException) {
            close("output stream unavailable: ${e.message}")
            return
        }
        try {
            while (!closed.get()) {
                pendingInfo.getAndSet(null)?.let { writeStreamInfo(out, it) }
                pendingState.getAndSet(null)?.let { writeDeviceState(out, it) }
                pendingPong.getAndSet(null)?.let { writePong(out, it) }

                val frame = queue.poll(HEARTBEAT_INTERVAL_MS)
                if (frame == null) {
                    if (System.nanoTime() - lastWriteNanos >= HEARTBEAT_INTERVAL_MS * 1_000_000L) {
                        writeControl(out, Protocol.TYPE_HEARTBEAT, 0, 0, 0L)
                    }
                    continue
                }
                try {
                    writeFrame(out, frame)
                } finally {
                    frame.recycle()
                }
            }
        } catch (e: IOException) {
            close("write failed: ${e.message}")
        } catch (t: Throwable) {
            close("writer crashed: $t")
        }
    }

    private fun writeFrame(out: OutputStream, frame: EncodedFrame) {
        Protocol.writeHeader(
            frame.data, 0,
            type = Protocol.TYPE_VIDEO_FRAME,
            flags = frame.flags,
            payloadLength = frame.size,
            sequence = frameSequence++,
            timestampUs = frame.ptsUs,
        )
        out.write(frame.data, 0, frame.wireSize)
        lastWriteNanos = System.nanoTime()
        framesSent++
        bytesSent += frame.wireSize
    }

    private fun writeStreamInfo(out: OutputStream, info: StreamInfo) {
        info.writeTo(controlBuf, Protocol.HEADER_SIZE)
        writeControl(out, Protocol.TYPE_STREAM_INFO, 0, Protocol.STREAM_INFO_SIZE, 0L)
    }

    private fun writeDeviceState(out: OutputStream, json: ByteArray) {
        // Rare and small: one fresh buffer so the packet still goes out in a single write().
        val packet = ByteArray(Protocol.HEADER_SIZE + json.size)
        Protocol.writeHeader(packet, 0, Protocol.TYPE_DEVICE_STATE, 0, json.size, 0, 0L)
        json.copyInto(packet, Protocol.HEADER_SIZE)
        out.write(packet)
        lastWriteNanos = System.nanoTime()
    }

    private fun writePong(out: OutputStream, pong: PendingPong) {
        Protocol.putLong(controlBuf, Protocol.HEADER_SIZE, pong.receivedUs)
        Protocol.putLong(controlBuf, Protocol.HEADER_SIZE + 8, nowUs())
        writeControl(out, Protocol.TYPE_PONG, 0, Protocol.PONG_SIZE, pong.echoUs)
    }

    /** Payload, if any, must already sit in [controlBuf] after the header. */
    private fun writeControl(out: OutputStream, type: Int, flags: Int, payloadLength: Int, timestampUs: Long) {
        Protocol.writeHeader(controlBuf, 0, type, flags, payloadLength, 0, timestampUs)
        out.write(controlBuf, 0, Protocol.HEADER_SIZE + payloadLength)
        lastWriteNanos = System.nanoTime()
    }

    private fun readLoop() {
        val header = PacketHeader()
        val headerBuf = ByteArray(Protocol.HEADER_SIZE)
        val payload = ByteArray(Protocol.MAX_CLIENT_PAYLOAD)
        try {
            socket.soTimeout = clientTimeoutMs
            val input = socket.getInputStream()
            while (!closed.get()) {
                readFully(input, headerBuf, Protocol.HEADER_SIZE)
                header.parse(headerBuf, 0, Protocol.MAX_CLIENT_PAYLOAD)
                readFully(input, payload, header.payloadLength)
                when (header.type) {
                    Protocol.TYPE_PING -> {
                        pendingPong.set(PendingPong(header.timestampUs, nowUs()))
                        queue.kick()
                    }
                    Protocol.TYPE_REQUEST_KEYFRAME -> listener.onSessionKeyframeRequest(this)
                    Protocol.TYPE_RECEIVER_REPORT ->
                        if (header.payloadLength >= Protocol.RECEIVER_REPORT_SIZE) {
                            listener.onSessionReport(this, ReceiverReport.parse(payload, 0))
                        }
                    Protocol.TYPE_CONTROL ->
                        listener.onSessionControl(this, ControlRequest.parse(payload, header.payloadLength))
                    else -> Unit // unknown types are skipped; the length was already consumed
                }
            }
        } catch (_: SocketTimeoutException) {
            close("client silent for ${clientTimeoutMs} ms")
        } catch (_: EOFException) {
            close("client disconnected")
        } catch (e: IOException) {
            close("read failed: ${e.message}")
        } catch (t: Throwable) {
            close("reader crashed: $t")
        }
    }

    companion object {
        private const val TAG = "AirLens.Session"
        const val HEARTBEAT_INTERVAL_MS = 1000L

        fun nowUs(): Long = System.nanoTime() / 1000L

        fun readFully(input: InputStream, dst: ByteArray, length: Int) {
            var read = 0
            while (read < length) {
                val n = input.read(dst, read, length - read)
                if (n < 0) throw EOFException()
                read += n
            }
        }
    }
}
