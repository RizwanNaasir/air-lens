using System.Buffers.Binary;

namespace AirLens.Core.Protocol;

/// <summary>
/// AirLens wire protocol v1. docs/PROTOCOL.md is the specification and
/// android/.../net/Protocol.kt is the other implementation; all three change together.
/// </summary>
public static class ProtocolConstants
{
    public const uint Magic = 0x414C4E53; // "ALNS"
    public const ushort Version = 1;
    public const int HeaderSize = 20;
    public const int MaxPayload = 16 * 1024 * 1024;
    public const int DefaultPort = 47474;

    public const int StreamInfoSize = 20;
    public const int HelloSize = 8;
    public const int PongSize = 16;
    public const int ReceiverReportSize = 12;
    public const int MaxDeviceState = 64 * 1024;
    public const int MaxControl = 4096;

    public const ushort CapHevc = 0x01;
    public const ushort Cap4K = 0x02;
}

public enum PacketType : byte
{
    // Server -> client
    StreamInfo = 0x01,
    VideoFrame = 0x02,
    Pong = 0x03,
    Heartbeat = 0x04,
    DeviceState = 0x05,

    // Client -> server
    Hello = 0x80,
    RequestKeyframe = 0x81,
    Ping = 0x82,
    ReceiverReport = 0x83,
    Control = 0x84,
}

[Flags]
public enum FrameFlags : byte
{
    None = 0,
    Keyframe = 0x01,
    CodecConfig = 0x02,
    Discontinuity = 0x04,
}

public enum VideoCodec : byte
{
    H264 = 1,
    Hevc = 2,
}

public sealed class ProtocolException(string message) : IOException(message);

/// <summary>The fixed 20-byte big-endian packet header.</summary>
public readonly record struct PacketHeader(PacketType Type, byte Flags, int PayloadLength, uint Sequence, long TimestampUs)
{
    public FrameFlags FrameFlags => (FrameFlags)Flags;

    /// <exception cref="ProtocolException">Payload length outside [0, <paramref name="maxPayload"/>].</exception>
    public static PacketHeader Read(ReadOnlySpan<byte> src, int maxPayload = ProtocolConstants.MaxPayload)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(src[4..]);
        if (length < 0 || length > maxPayload)
            throw new ProtocolException($"payload length {length} outside 0..{maxPayload} (type 0x{src[0]:X2})");
        return new PacketHeader(
            (PacketType)src[0],
            src[1],
            length,
            BinaryPrimitives.ReadUInt32BigEndian(src[8..]),
            BinaryPrimitives.ReadInt64BigEndian(src[12..]));
    }

    public void Write(Span<byte> dst)
    {
        dst[0] = (byte)Type;
        dst[1] = Flags;
        dst[2] = 0;
        dst[3] = 0;
        BinaryPrimitives.WriteInt32BigEndian(dst[4..], PayloadLength);
        BinaryPrimitives.WriteUInt32BigEndian(dst[8..], Sequence);
        BinaryPrimitives.WriteInt64BigEndian(dst[12..], TimestampUs);
    }
}

/// <summary>
/// Describes the elementary stream. A changed <see cref="Epoch"/> means the phone restarted its
/// encoder: flush the decoder and wait for the next keyframe.
/// </summary>
public readonly record struct StreamInfo(
    VideoCodec Codec,
    int Width,
    int Height,
    int Fps,
    int BitrateBps,
    /// <summary>Clockwise rotation the receiver must still apply (direct mode only).</summary>
    int RotationDegrees,
    bool FullRange,
    /// <summary>Receiver must mirror horizontally (direct mode only).</summary>
    bool MirrorRequired,
    uint Epoch)
{
    public static StreamInfo Parse(ReadOnlySpan<byte> p)
    {
        if (p.Length < ProtocolConstants.StreamInfoSize) throw new ProtocolException($"STREAM_INFO too short ({p.Length})");
        return new StreamInfo(
            (VideoCodec)p[0],
            BinaryPrimitives.ReadUInt16BigEndian(p[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(p[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(p[6..]),
            BinaryPrimitives.ReadInt32BigEndian(p[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(p[12..]),
            (p[1] & 0x01) != 0,
            (p[1] & 0x02) != 0,
            BinaryPrimitives.ReadUInt32BigEndian(p[16..]));
    }

    public override string ToString() =>
        $"{(Codec == VideoCodec.Hevc ? "H.265" : "H.264")} {Width}x{Height}@{Fps} {BitrateBps / 1_000_000.0:0.#} Mbps epoch {Epoch}";
}

/// <summary>Builders for the client packets. The fixed-size ones write into caller-provided buffers.</summary>
public static class ClientPackets
{
    public const int HelloLength = ProtocolConstants.HeaderSize + ProtocolConstants.HelloSize;
    public const int ReportLength = ProtocolConstants.HeaderSize + ProtocolConstants.ReceiverReportSize;

    public static int WriteHello(Span<byte> dst, ushort capabilities)
    {
        new PacketHeader(PacketType.Hello, 0, ProtocolConstants.HelloSize, 0, 0).Write(dst);
        BinaryPrimitives.WriteUInt32BigEndian(dst[20..], ProtocolConstants.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(dst[24..], ProtocolConstants.Version);
        BinaryPrimitives.WriteUInt16BigEndian(dst[26..], capabilities);
        return HelloLength;
    }

    public static int WritePing(Span<byte> dst, long clientTimeUs)
    {
        new PacketHeader(PacketType.Ping, 0, 0, 0, clientTimeUs).Write(dst);
        return ProtocolConstants.HeaderSize;
    }

    public static int WriteRequestKeyframe(Span<byte> dst)
    {
        new PacketHeader(PacketType.RequestKeyframe, 0, 0, 0, 0).Write(dst);
        return ProtocolConstants.HeaderSize;
    }

    /// <summary>
    /// CONTROL: ask the phone for another lens, quality preset or focus, or tell it the virtual
    /// camera is on. <paramref name="focusLock"/> fixes focus wherever autofocus has it right now.
    /// Allocates; it is sent on a click, not per frame.
    /// </summary>
    public static byte[] BuildControl(string? lens = null, string? quality = null, double? focus = null, bool focusAuto = false, bool? virtualCamera = null, bool focusLock = false)
    {
        var json = new System.Buffers.ArrayBufferWriter<byte>(64);
        using (var w = new System.Text.Json.Utf8JsonWriter(json))
        {
            w.WriteStartObject();
            if (lens != null) w.WriteString("lens", lens);
            if (quality != null) w.WriteString("quality", quality);
            if (focusLock) w.WriteString("focus", "lock");
            else if (focusAuto) w.WriteString("focus", "auto");
            else if (focus is { } f) w.WriteNumber("focus", Math.Round(Math.Clamp(f, 0, 1), 3));
            if (virtualCamera is { } vc) w.WriteBoolean("virtualCamera", vc);
            w.WriteEndObject();
        }
        var packet = new byte[ProtocolConstants.HeaderSize + json.WrittenCount];
        new PacketHeader(PacketType.Control, 0, json.WrittenCount, 0, 0).Write(packet);
        json.WrittenSpan.CopyTo(packet.AsSpan(ProtocolConstants.HeaderSize));
        return packet;
    }

    public static int WriteReceiverReport(Span<byte> dst, uint framesDecoded, uint framesDropped, ushort decodeBacklogMs)
    {
        new PacketHeader(PacketType.ReceiverReport, 0, ProtocolConstants.ReceiverReportSize, 0, 0).Write(dst);
        BinaryPrimitives.WriteUInt32BigEndian(dst[20..], framesDecoded);
        BinaryPrimitives.WriteUInt32BigEndian(dst[24..], framesDropped);
        BinaryPrimitives.WriteUInt16BigEndian(dst[28..], decodeBacklogMs);
        BinaryPrimitives.WriteUInt16BigEndian(dst[30..], 0);
        return ReportLength;
    }
}
