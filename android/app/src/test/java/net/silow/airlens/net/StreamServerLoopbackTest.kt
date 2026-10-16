package net.silow.airlens.net

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.DataInputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/** Drives a real [StreamServer] over loopback with a hand-written client that speaks the spec. */
class StreamServerLoopbackTest {
    private val pool = EncodedFramePool()
    private val keyframeRequests = AtomicInteger()
    private val disconnects = CopyOnWriteArrayList<String>()
    private val controls = java.util.concurrent.LinkedBlockingQueue<ControlRequest>()
    private val connected = CountDownLatch(1)
    private lateinit var server: StreamServer

    private val info = StreamInfo(Protocol.CODEC_H264, 1920, 1080, 60, 12_000_000, 0, false, false, epoch = 1)

    @Before
    fun setUp() {
        server = StreamServer(
            port = 0,
            pool = pool,
            clientTimeoutMs = 600,
            listener = object : StreamServer.Listener {
                override fun onClientConnected(remoteAddress: String) = connected.countDown()
                override fun onClientDisconnected(remoteAddress: String, reason: String) {
                    disconnects += reason
                }
                override fun onKeyframeNeeded() {
                    keyframeRequests.incrementAndGet()
                }
                override fun onCongestion() = Unit
                override fun onReceiverReport(report: ReceiverReport) = Unit
                override fun onControl(request: ControlRequest) {
                    controls += request
                }
            },
        )
        server.start()
        server.updateStreamInfo(info)
    }

    @After
    fun tearDown() {
        server.stop()
    }

    private fun connect(): Pair<Socket, DataInputStream> {
        val s = Socket()
        s.connect(InetSocketAddress("127.0.0.1", server.boundPort), 1000)
        s.soTimeout = 2000
        val hello = ByteArray(Protocol.HEADER_SIZE + Protocol.HELLO_SIZE)
        Protocol.writeHeader(hello, 0, Protocol.TYPE_HELLO, 0, Protocol.HELLO_SIZE, 0, 0)
        Protocol.putInt(hello, 20, Protocol.MAGIC)
        Protocol.putShort(hello, 24, Protocol.VERSION)
        s.getOutputStream().write(hello)
        return s to DataInputStream(s.getInputStream())
    }

    private class Packet(val header: PacketHeader, val payload: ByteArray)

    private fun DataInputStream.readPacket(): Packet {
        val hb = ByteArray(Protocol.HEADER_SIZE)
        readFully(hb)
        val h = PacketHeader().parse(hb)
        val p = ByteArray(h.payloadLength)
        readFully(p)
        return Packet(h, p)
    }

    /** Skips heartbeats, which the server may interleave at any time. */
    private fun DataInputStream.readNonHeartbeat(): Packet {
        while (true) {
            val p = readPacket()
            if (p.header.type != Protocol.TYPE_HEARTBEAT) return p
        }
    }

    private fun submit(flags: Int, payload: ByteArray, pts: Long) {
        val f = pool.acquire()
        f.append(payload, 0, payload.size)
        f.flags = flags
        f.ptsUs = pts
        server.submit(f)
    }

    private fun awaitSession() {
        assertTrue(connected.await(2, TimeUnit.SECONDS))
        val deadline = System.nanoTime() + 2_000_000_000L
        while (server.activeSession == null && System.nanoTime() < deadline) Thread.sleep(5)
    }

    @Test
    fun handshakeThenStreamInfoConfigAndFramesInOrder() {
        // Config cached before the client connects must be replayed to it.
        submit(Protocol.FLAG_CODEC_CONFIG, byteArrayOf(0, 0, 0, 1, 0x67), 0)
        val (socket, input) = connect()
        socket.use {
            awaitSession()
            assertTrue("new client must trigger an IDR request", keyframeRequests.get() >= 1)

            val first = input.readNonHeartbeat()
            assertEquals(Protocol.TYPE_STREAM_INFO, first.header.type)
            assertEquals(1920, Protocol.getUShort(first.payload, 2))

            val config = input.readNonHeartbeat()
            assertEquals(Protocol.TYPE_VIDEO_FRAME, config.header.type)
            assertEquals(Protocol.FLAG_CODEC_CONFIG, config.header.flags)

            submit(0, byteArrayOf(1, 2, 3), 10) // dropped: no keyframe yet
            submit(Protocol.FLAG_KEYFRAME, byteArrayOf(9, 9, 9, 9), 20)
            submit(0, byteArrayOf(4, 5), 30)

            val idr = input.readNonHeartbeat()
            assertEquals(Protocol.FLAG_KEYFRAME, idr.header.flags)
            assertEquals(20L, idr.header.timestampUs)
            assertEquals(4, idr.payload.size)

            val p = input.readNonHeartbeat()
            assertEquals(30L, p.header.timestampUs)
            assertEquals(idr.header.sequence + 1, p.header.sequence)
        }
    }

    @Test
    fun deviceStateFollowsStreamInfoAndControlReachesTheListener() {
        server.updateDeviceState("""{"device":"Test phone","lens":"2"}""".toByteArray())
        val (socket, input) = connect()
        socket.use {
            awaitSession()
            assertEquals(Protocol.TYPE_STREAM_INFO, input.readNonHeartbeat().header.type)
            val state = input.readNonHeartbeat()
            assertEquals(Protocol.TYPE_DEVICE_STATE, state.header.type)
            assertTrue(String(state.payload, Charsets.UTF_8).contains("Test phone"))

            val json = """{"quality":"fast"}""".toByteArray()
            val packet = ByteArray(Protocol.HEADER_SIZE + json.size)
            Protocol.writeHeader(packet, 0, Protocol.TYPE_CONTROL, 0, json.size, 0, 0L)
            json.copyInto(packet, Protocol.HEADER_SIZE)
            socket.getOutputStream().write(packet)
            assertEquals("fast", controls.poll(2, TimeUnit.SECONDS)?.quality)

            // A later state reaches the live session too.
            server.updateDeviceState("""{"device":"Test phone","lens":"3"}""".toByteArray())
            val update = input.readNonHeartbeat()
            assertEquals(Protocol.TYPE_DEVICE_STATE, update.header.type)
            assertTrue(String(update.payload, Charsets.UTF_8).contains("\"3\""))
        }
    }

    @Test
    fun pingIsAnsweredWithPong() {
        val (socket, input) = connect()
        socket.use {
            awaitSession()
            val ping = ByteArray(Protocol.HEADER_SIZE)
            Protocol.writeHeader(ping, 0, Protocol.TYPE_PING, 0, 0, 0, 123_456L)
            socket.getOutputStream().write(ping)
            var pong: Packet
            do pong = input.readPacket() while (pong.header.type != Protocol.TYPE_PONG)
            assertEquals(123_456L, pong.header.timestampUs)
            assertEquals(Protocol.PONG_SIZE, pong.payload.size)
        }
    }

    @Test
    fun newClientPreemptsOldOne() {
        val (first, firstIn) = connect()
        first.use {
            awaitSession()
            val (second, _) = connect()
            second.use {
                // The first socket must be closed by the server: reads hit EOF.
                val deadline = System.nanoTime() + 2_000_000_000L
                var eof = false
                while (!eof && System.nanoTime() < deadline) {
                    eof = try {
                        firstIn.readPacket()
                        false
                    } catch (_: java.io.EOFException) {
                        true
                    } catch (_: java.net.SocketException) {
                        true
                    }
                }
                assertTrue("old session must be closed", eof)
            }
        }
    }

    @Test
    fun silentClientIsDisconnectedAndFramesAreRecycled() {
        val (socket, _) = connect()
        socket.use {
            awaitSession()
            submit(Protocol.FLAG_KEYFRAME, ByteArray(1000), 1)
            // No pings: the 600 ms watchdog closes the session.
            val deadline = System.nanoTime() + 3_000_000_000L
            while (disconnects.isEmpty() && System.nanoTime() < deadline) Thread.sleep(20)
            assertTrue(disconnects.single().contains("silent"))
            assertEquals(0, pool.outstandingCount)
        }
    }

    @Test
    fun badHelloIsRejectedWithoutDisturbingServer() {
        Socket("127.0.0.1", server.boundPort).use { bad ->
            bad.getOutputStream().write(ByteArray(Protocol.HEADER_SIZE + 8) { 0x7F })
            bad.soTimeout = 2000
            assertEquals(-1, bad.getInputStream().read())
        }
        val (socket, input) = connect()
        socket.use {
            awaitSession()
            assertEquals(Protocol.TYPE_STREAM_INFO, input.readNonHeartbeat().header.type)
        }
    }
}
