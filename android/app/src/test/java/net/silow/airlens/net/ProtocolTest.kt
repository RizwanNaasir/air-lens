package net.silow.airlens.net

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

/** The byte vectors here are copied from docs/PROTOCOL.md; the Windows tests use the same ones. */
class ProtocolTest {
    private fun hex(s: String): ByteArray =
        s.split(" ").filter { it.isNotBlank() }.map { it.toInt(16).toByte() }.toByteArray()

    @Test
    fun videoFrameHeaderMatchesGoldenVector() {
        val buf = ByteArray(Protocol.HEADER_SIZE)
        Protocol.writeHeader(buf, 0, Protocol.TYPE_VIDEO_FRAME, Protocol.FLAG_KEYFRAME, 5, 7, 1_000_000L)
        assertArrayEquals(hex("02 01 00 00 00 00 00 05 00 00 00 07 00 00 00 00 00 0F 42 40"), buf)
    }

    @Test
    fun controlPacketMatchesGoldenVector() {
        val json = """{"lens":"3"}""".toByteArray()
        val buf = ByteArray(Protocol.HEADER_SIZE + json.size)
        Protocol.writeHeader(buf, 0, Protocol.TYPE_CONTROL, 0, json.size, 0, 0L)
        json.copyInto(buf, Protocol.HEADER_SIZE)
        assertArrayEquals(
            hex("84 00 00 00 00 00 00 0C 00 00 00 00 00 00 00 00 00 00 00 00 7B 22 6C 65 6E 73 22 3A 22 33 22 7D"),
            buf,
        )
    }

    @Test
    fun helloPacketMatchesGoldenVector() {
        val buf = ByteArray(Protocol.HEADER_SIZE + Protocol.HELLO_SIZE)
        Protocol.writeHeader(buf, 0, Protocol.TYPE_HELLO, 0, Protocol.HELLO_SIZE, 0, 0L)
        Protocol.putInt(buf, 20, Protocol.MAGIC)
        Protocol.putShort(buf, 24, Protocol.VERSION)
        Protocol.putShort(buf, 26, Protocol.CAP_HEVC or Protocol.CAP_4K)
        assertArrayEquals(
            hex("80 00 00 00 00 00 00 08 00 00 00 00 00 00 00 00 00 00 00 00 41 4C 4E 53 00 01 00 03"),
            buf,
        )
    }

    @Test
    fun headerRoundTripsIncludingNegativeAndHighBitValues() {
        val buf = ByteArray(Protocol.HEADER_SIZE)
        Protocol.writeHeader(buf, 0, 0x83, 0xFF, 1234, -1, -42L)
        val h = PacketHeader().parse(buf)
        assertEquals(0x83, h.type)
        assertEquals(0xFF, h.flags)
        assertEquals(1234, h.payloadLength)
        assertEquals(-1, h.sequence)
        assertEquals(-42L, h.timestampUs)
    }

    @Test(expected = ProtocolException::class)
    fun oversizedPayloadIsRejected() {
        val buf = ByteArray(Protocol.HEADER_SIZE)
        Protocol.writeHeader(buf, 0, Protocol.TYPE_PING, 0, Protocol.MAX_CLIENT_PAYLOAD + 1, 0, 0L)
        PacketHeader().parse(buf, 0, Protocol.MAX_CLIENT_PAYLOAD)
    }

    @Test
    fun streamInfoLayout() {
        val buf = ByteArray(Protocol.STREAM_INFO_SIZE)
        StreamInfo(Protocol.CODEC_HEVC, 1920, 1080, 60, 16_000_000, 90, fullRange = false, mirrored = true, epoch = 3)
            .writeTo(buf, 0)
        assertArrayEquals(
            hex("02 02 07 80 04 38 00 3C 00 F4 24 00 00 5A 00 00 00 00 00 03"),
            buf,
        )
    }
}
