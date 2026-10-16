package net.silow.airlens.net

import net.silow.airlens.util.DeviceId
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

class DiscoveryResponderTest {
    private var busy = false
    private val responder = DiscoveryResponder(0) { DiscoveryResponder.announcePacket("abc123", "Test phone", 47474, busy) }

    @After
    fun tearDown() = responder.close()

    private fun ask(query: ByteArray): ByteArray? = DatagramSocket().use { s ->
        s.soTimeout = 1000
        s.send(DatagramPacket(query, query.size, InetAddress.getLoopbackAddress(), responder.boundPort))
        val buf = ByteArray(1500)
        val reply = DatagramPacket(buf, buf.size)
        try {
            s.receive(reply)
            buf.copyOf(reply.length)
        } catch (_: SocketTimeoutException) {
            null
        }
    }

    @Test
    fun queryMatchesGoldenVector() {
        val expected = "41 4C 4E 53 00 01 01 00".split(" ").map { it.toInt(16).toByte() }.toByteArray()
        assertArrayEquals(expected, DiscoveryResponder.queryPacket())
    }

    @Test
    fun answersAQueryWithItsNameAndId() {
        responder.start()
        busy = true
        val reply = ask(DiscoveryResponder.queryPacket())!!
        assertEquals(Protocol.MAGIC, Protocol.getInt(reply, 0))
        assertEquals(DiscoveryResponder.TYPE_ANNOUNCE, reply[6].toInt())
        val json = JSONObject(String(reply, DiscoveryResponder.HEADER_SIZE, reply.size - DiscoveryResponder.HEADER_SIZE, Charsets.UTF_8))
        assertEquals("abc123", json.getString("id"))
        assertEquals("Test phone", json.getString("device"))
        assertEquals(47474, json.getInt("port"))
        assertTrue(json.getBoolean("busy"))
    }

    @Test
    fun ignoresAnythingThatIsNotAQuery() {
        responder.start()
        val junk = "hello there".toByteArray()
        assertEquals(null, ask(junk))
        assertFalse(DiscoveryResponder.isQuery(DiscoveryResponder.announcePacket("x", "y", 1, false), 8))
        // Still answering afterwards.
        assertTrue(ask(DiscoveryResponder.queryPacket()) != null)
    }

    @Test
    fun deviceIdIsStableShortAndNotTheAndroidId() {
        val a = DeviceId.of("9774d56d682e549c")
        assertEquals(a, DeviceId.of("9774d56d682e549c"))
        assertEquals(16, a.length)
        assertFalse(a.contains("9774d56d"))
        assertFalse(a == DeviceId.of("0000000000000000"))
    }
}
