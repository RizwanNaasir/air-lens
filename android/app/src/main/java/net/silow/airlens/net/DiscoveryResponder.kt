package net.silow.airlens.net

import net.silow.airlens.util.AirLog
import org.json.JSONObject
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress

/**
 * Answers the PC's discovery broadcasts (docs/PROTOCOL.md, "Discovery") so nobody has to type the
 * phone's address. One thread in a blocking receive; [close] unblocks it by closing the socket.
 *
 * [announce] is asked for a fresh reply on every query, so `busy` is always current.
 */
class DiscoveryResponder(private val port: Int, private val announce: () -> ByteArray) {
    private var socket: DatagramSocket? = null
    private var thread: Thread? = null
    @Volatile private var running = false

    /** The bound UDP port (useful with port 0 in tests), or -1 when not started. */
    val boundPort: Int get() = socket?.localPort ?: -1

    @Throws(IOException::class)
    fun start() {
        val listenPort = port // inside apply {}, `port` would be DatagramSocket.getPort() (-1)
        val s = DatagramSocket(null).apply {
            reuseAddress = true
            broadcast = true
            bind(InetSocketAddress(listenPort))
        }
        socket = s
        running = true
        thread = Thread({ loop(s) }, "AirLens-Discovery").apply {
            isDaemon = true
            start()
        }
        AirLog.i(TAG, "answering discovery on UDP ${s.localPort}")
    }

    private fun loop(s: DatagramSocket) {
        val buf = ByteArray(64)
        while (running) {
            val packet = DatagramPacket(buf, buf.size)
            try {
                s.receive(packet)
            } catch (e: IOException) {
                if (!running) return
                continue
            }
            if (!isQuery(buf, packet.length)) continue
            val reply = runCatching(announce).getOrNull() ?: continue
            try {
                s.send(DatagramPacket(reply, reply.size, packet.socketAddress))
            } catch (e: IOException) {
                AirLog.w(TAG, "discovery reply to ${packet.socketAddress} failed: ${e.message}")
            }
        }
    }

    fun close() {
        running = false
        socket?.close()
        thread?.join(500)
        socket = null
        thread = null
    }

    companion object {
        private const val TAG = "AirLens.Discovery"
        const val HEADER_SIZE = 8
        const val TYPE_QUERY = 0x01
        const val TYPE_ANNOUNCE = 0x02

        fun isQuery(b: ByteArray, length: Int): Boolean =
            length >= HEADER_SIZE &&
                Protocol.getInt(b, 0) == Protocol.MAGIC &&
                Protocol.getUShort(b, 4) == Protocol.VERSION &&
                (b[6].toInt() and 0xFF) == TYPE_QUERY

        fun queryPacket(): ByteArray = header(TYPE_QUERY)

        fun announcePacket(id: String, device: String, port: Int, busy: Boolean): ByteArray {
            val json = JSONObject().put("id", id).put("device", device).put("port", port).put("busy", busy)
                .toString().toByteArray(Charsets.UTF_8)
            return header(TYPE_ANNOUNCE, json.size).also { json.copyInto(it, HEADER_SIZE) }
        }

        private fun header(type: Int, extra: Int = 0): ByteArray = ByteArray(HEADER_SIZE + extra).also {
            Protocol.putInt(it, 0, Protocol.MAGIC)
            Protocol.putShort(it, 4, Protocol.VERSION)
            it[6] = type.toByte()
            it[7] = 0
        }
    }
}
