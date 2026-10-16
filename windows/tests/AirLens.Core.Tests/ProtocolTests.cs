using AirLens.Core.Protocol;
using Xunit;

namespace AirLens.Core.Tests;

/// <summary>Byte vectors are copied from docs/PROTOCOL.md; the Android tests use the same ones.</summary>
public class ProtocolTests
{
    private static byte[] Hex(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

    [Fact]
    public void VideoFrameHeaderMatchesGoldenVector()
    {
        var buf = new byte[ProtocolConstants.HeaderSize];
        new PacketHeader(PacketType.VideoFrame, (byte)FrameFlags.Keyframe, 5, 7, 1_000_000).Write(buf);
        Assert.Equal(Hex("02 01 00 00 00 00 00 05 00 00 00 07 00 00 00 00 00 0F 42 40"), buf);
    }

    [Fact]
    public void HelloPacketMatchesGoldenVector()
    {
        var buf = new byte[ClientPackets.HelloLength];
        ClientPackets.WriteHello(buf, ProtocolConstants.CapHevc | ProtocolConstants.Cap4K);
        Assert.Equal(Hex("80 00 00 00 00 00 00 08 00 00 00 00 00 00 00 00 00 00 00 00 41 4C 4E 53 00 01 00 03"), buf);
    }

    [Fact]
    public void ControlPacketMatchesGoldenVector()
    {
        Assert.Equal(
            Hex("84 00 00 00 00 00 00 0C 00 00 00 00 00 00 00 00 00 00 00 00 7B 22 6C 65 6E 73 22 3A 22 33 22 7D"),
            ClientPackets.BuildControl("3", null));
    }

    [Fact]
    public void DeviceStateParsesTheDocumentedExample()
    {
        var json = """
            {"device":"HUAWEI P30","lens":"2",
             "lenses":[{"key":"3","label":"Ultra-wide","kind":"ultra_wide"},{"key":"2","label":"Main","kind":"wide"}],
             "quality":"best",
             "presets":[{"id":"best","label":"4K","tag":"Best picture","width":3840,"height":2160,"fps":30,"codec":"hevc","bitrateBps":80000000,"direct":false},
                        {"id":"fast","label":"1080p","tag":"Lowest delay","width":1920,"height":1080,"fps":30,"codec":"h264","bitrateBps":60000000,"direct":true}],
             "future":{"ignored":true}}
            """u8;
        var s = DeviceState.TryParse(json)!;
        Assert.Equal("HUAWEI P30", s.Device);
        Assert.Equal("Main", s.ActiveLens!.Label);
        Assert.Equal(2, s.Lenses.Count);
        Assert.Equal("4K · Best picture", s.ActivePreset!.Title);
        Assert.Equal("30 fps · H.265 · 80 Mbps", s.ActivePreset.Summary);
        Assert.True(s.Presets[1].Direct);
        Assert.Equal(VideoCodec.H264, s.Presets[1].Codec);
    }

    [Fact]
    public void DeviceStateIsLenientAndRejectsNonJson()
    {
        Assert.Null(DeviceState.TryParse("not json"u8));
        var minimal = DeviceState.TryParse("{}"u8)!;
        Assert.Equal(DeviceState.Custom, minimal.Quality);
        Assert.Empty(minimal.Lenses);
        Assert.Null(minimal.ActivePreset);
        var front = new QualityPresetInfo("best", "1440p", "Best picture", 1920, 1440, 30, VideoCodec.Hevc, 80_000_000, false);
        Assert.Equal("30 fps · 4:3 · H.265 · 80 Mbps", front.Summary);
    }

    [Fact]
    public void FocusHeatAndControlExtrasRoundTrip()
    {
        var s = DeviceState.TryParse("""{"device":"P","focus":{"supported":true,"auto":false,"value":0.25},"heat":{"tempC":44.1,"level":"hot","lowered":"1440p"}}"""u8)!;
        Assert.True(s.Focus!.Supported);
        Assert.Equal(0.25, s.Focus.Value);
        Assert.True(s.Heat!.IsHot);
        Assert.Equal("1440p", s.Heat.Lowered);
        Assert.Null(DeviceState.TryParse("""{"focus":{"supported":true,"auto":true}}"""u8)!.Focus!.Value);
        string Json(byte[] p) => System.Text.Encoding.UTF8.GetString(p, ProtocolConstants.HeaderSize, p.Length - ProtocolConstants.HeaderSize);
        Assert.Equal("""{"focus":"auto"}""", Json(ClientPackets.BuildControl(focusAuto: true)));
        Assert.Equal("""{"focus":"lock"}""", Json(ClientPackets.BuildControl(focusLock: true)));
        Assert.Equal("""{"focus":0.4,"virtualCamera":true}""", Json(ClientPackets.BuildControl(focus: 0.4, virtualCamera: true)));
    }

    [Fact]
    public void StreamInfoParsesAndroidGoldenVector()
    {
        var info = StreamInfo.Parse(Hex("02 02 07 80 04 38 00 3C 00 F4 24 00 00 5A 00 00 00 00 00 03"));
        Assert.Equal(VideoCodec.Hevc, info.Codec);
        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Equal(60, info.Fps);
        Assert.Equal(16_000_000, info.BitrateBps);
        Assert.Equal(90, info.RotationDegrees);
        Assert.False(info.FullRange);
        Assert.True(info.MirrorRequired);
        Assert.Equal(3u, info.Epoch);
    }

    [Fact]
    public void HeaderRoundTripsHighBitValues()
    {
        var buf = new byte[ProtocolConstants.HeaderSize];
        new PacketHeader(PacketType.ReceiverReport, 0xFF, 1234, uint.MaxValue, -42).Write(buf);
        var h = PacketHeader.Read(buf);
        Assert.Equal(PacketType.ReceiverReport, h.Type);
        Assert.Equal(0xFF, h.Flags);
        Assert.Equal(1234, h.PayloadLength);
        Assert.Equal(uint.MaxValue, h.Sequence);
        Assert.Equal(-42, h.TimestampUs);
    }

    [Fact]
    public void OversizedOrNegativeLengthIsAProtocolError()
    {
        var buf = new byte[ProtocolConstants.HeaderSize];
        new PacketHeader(PacketType.VideoFrame, 0, ProtocolConstants.MaxPayload + 1, 0, 0).Write(buf);
        Assert.Throws<ProtocolException>(() => PacketHeader.Read(buf));
        new PacketHeader(PacketType.VideoFrame, 0, -1, 0, 0).Write(buf);
        Assert.Throws<ProtocolException>(() => PacketHeader.Read(buf));
    }
}
