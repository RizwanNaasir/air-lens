package net.silow.airlens.net

/**
 * AirLens wire protocol v1. docs/PROTOCOL.md is the specification and
 * windows/src/AirLens.Core/Protocol is the other implementation; all three change together.
 *
 * Every packet is a fixed 20-byte big-endian header followed by `payloadLength` bytes.
 * Encoding works on plain byte arrays so the hot path can write headers in place (see [EncodedFrame]).
 */
object Protocol {
    const val MAGIC = 0x414C4E53 // "ALNS"
    const val VERSION = 1
    const val HEADER_SIZE = 20
    const val MAX_PAYLOAD = 16 * 1024 * 1024
    const val DEFAULT_PORT = 47474

    /** Client packets are tiny; anything larger is a confused or hostile peer. */
    const val MAX_CLIENT_PAYLOAD = 4096

    // Server -> client
    const val TYPE_STREAM_INFO = 0x01
    const val TYPE_VIDEO_FRAME = 0x02
    const val TYPE_PONG = 0x03
    const val TYPE_HEARTBEAT = 0x04
    const val TYPE_DEVICE_STATE = 0x05

    // Client -> server
    const val TYPE_HELLO = 0x80
    const val TYPE_REQUEST_KEYFRAME = 0x81
    const val TYPE_PING = 0x82
    const val TYPE_RECEIVER_REPORT = 0x83
    const val TYPE_CONTROL = 0x84

    /** DEVICE_STATE is JSON; a few hundred bytes in practice. */
    const val MAX_DEVICE_STATE = 64 * 1024

    // VIDEO_FRAME flags
    const val FLAG_KEYFRAME = 0x01
    const val FLAG_CODEC_CONFIG = 0x02
    const val FLAG_DISCONTINUITY = 0x04

    const val CODEC_H264 = 1
    const val CODEC_HEVC = 2

    const val STREAM_INFO_SIZE = 20
    const val HELLO_SIZE = 8
    const val PONG_SIZE = 16
    const val RECEIVER_REPORT_SIZE = 12

    const val CAP_HEVC = 0x01
    const val CAP_4K = 0x02

    fun writeHeader(
        dst: ByteArray,
        offset: Int,
        type: Int,
        flags: Int,
        payloadLength: Int,
        sequence: Int,
        timestampUs: Long,
    ) {
        dst[offset] = type.toByte()
        dst[offset + 1] = flags.toByte()
        dst[offset + 2] = 0
        dst[offset + 3] = 0
        putInt(dst, offset + 4, payloadLength)
        putInt(dst, offset + 8, sequence)
        putLong(dst, offset + 12, timestampUs)
    }

    fun putShort(dst: ByteArray, offset: Int, value: Int) {
        dst[offset] = (value ushr 8).toByte()
        dst[offset + 1] = value.toByte()
    }

    fun putInt(dst: ByteArray, offset: Int, value: Int) {
        dst[offset] = (value ushr 24).toByte()
        dst[offset + 1] = (value ushr 16).toByte()
        dst[offset + 2] = (value ushr 8).toByte()
        dst[offset + 3] = value.toByte()
    }

    fun putLong(dst: ByteArray, offset: Int, value: Long) {
        putInt(dst, offset, (value ushr 32).toInt())
        putInt(dst, offset + 4, value.toInt())
    }

    fun getUShort(src: ByteArray, offset: Int): Int =
        ((src[offset].toInt() and 0xFF) shl 8) or (src[offset + 1].toInt() and 0xFF)

    fun getInt(src: ByteArray, offset: Int): Int =
        ((src[offset].toInt() and 0xFF) shl 24) or
            ((src[offset + 1].toInt() and 0xFF) shl 16) or
            ((src[offset + 2].toInt() and 0xFF) shl 8) or
            (src[offset + 3].toInt() and 0xFF)

    fun getLong(src: ByteArray, offset: Int): Long =
        (getInt(src, offset).toLong() shl 32) or (getInt(src, offset + 4).toLong() and 0xFFFFFFFFL)
}

/** A parsed header. Mutable so a reader thread can reuse one instance for its whole life. */
class PacketHeader {
    var type = 0
    var flags = 0
    var payloadLength = 0
    var sequence = 0
    var timestampUs = 0L

    /** @throws ProtocolException when the length field is outside [0, maxPayload]. */
    fun parse(src: ByteArray, offset: Int = 0, maxPayload: Int = Protocol.MAX_PAYLOAD): PacketHeader {
        type = src[offset].toInt() and 0xFF
        flags = src[offset + 1].toInt() and 0xFF
        payloadLength = Protocol.getInt(src, offset + 4)
        sequence = Protocol.getInt(src, offset + 8)
        timestampUs = Protocol.getLong(src, offset + 12)
        if (payloadLength < 0 || payloadLength > maxPayload) {
            throw ProtocolException("payload length $payloadLength outside 0..$maxPayload (type 0x${type.toString(16)})")
        }
        return this
    }
}

class ProtocolException(message: String) : java.io.IOException(message)

/**
 * Describes the elementary stream. Sent at session start and whenever the encoder is reconfigured;
 * a changed [epoch] tells the receiver to flush its decoder.
 */
data class StreamInfo(
    val codec: Int,
    val width: Int,
    val height: Int,
    val fps: Int,
    val bitrateBps: Int,
    /** Clockwise rotation the receiver must still apply. Non-zero only in direct (no-GL) mode. */
    val rotationDegrees: Int,
    val fullRange: Boolean,
    val mirrored: Boolean,
    val epoch: Int,
) {
    fun writeTo(dst: ByteArray, offset: Int) {
        dst[offset] = codec.toByte()
        dst[offset + 1] = ((if (fullRange) 0x01 else 0) or (if (mirrored) 0x02 else 0)).toByte()
        Protocol.putShort(dst, offset + 2, width)
        Protocol.putShort(dst, offset + 4, height)
        Protocol.putShort(dst, offset + 6, fps)
        Protocol.putInt(dst, offset + 8, bitrateBps)
        Protocol.putShort(dst, offset + 12, rotationDegrees)
        Protocol.putShort(dst, offset + 14, 0)
        Protocol.putInt(dst, offset + 16, epoch)
    }
}

/** Sent by the PC once a second; drives the sender's bitrate controller. */
data class ReceiverReport(
    val framesDecoded: Long,
    val framesDropped: Long,
    val decodeBacklogMs: Int,
) {
    companion object {
        fun parse(src: ByteArray, offset: Int): ReceiverReport =
            ReceiverReport(
                framesDecoded = Protocol.getInt(src, offset).toLong() and 0xFFFFFFFFL,
                framesDropped = Protocol.getInt(src, offset + 4).toLong() and 0xFFFFFFFFL,
                decodeBacklogMs = Protocol.getUShort(src, offset + 8),
            )
    }
}
