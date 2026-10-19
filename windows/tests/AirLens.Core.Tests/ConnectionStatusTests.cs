using System.Buffers.Binary;
using System.Net;
using AirLens.Core.Pipeline;
using AirLens.Core.Protocol;
using AirLens.Core.Status;
using AirLens.Core.Transport;
using Xunit;

namespace AirLens.Core.Tests;

public class ConnectionStatusTests
{
    private static ReceiverStats Stats(ReceiverState state, StreamEndpoint? endpoint = null, bool withStream = false, bool withDevice = false, double rtt = 0) =>
        new(state, "", endpoint, withStream ? Info() : null, "", null, 0, 0, rtt, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false,
            withDevice ? DeviceState.TryParse("""{"device":"HUAWEI P30","id":"p30","heat":{"tempC":39.6,"level":"normal"}}"""u8.ToArray()) : null);

    private static StreamInfo Info()
    {
        var p = new byte[ProtocolConstants.StreamInfoSize];
        p[0] = (byte)VideoCodec.H264;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 1920);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), 1080);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), 30);
        return StreamInfo.Parse(p);
    }

    private static ConnectionStatus Status(ReceiverStats? stats = null, bool looking = true, UsbPresence usb = UsbPresence.NoPhone,
        IReadOnlyList<DiscoveredPhone>? wifi = null, string? error = null, TimeSpan? streamingFor = null) =>
        ConnectionStatus.From(new ConnectionInputs(stats, looking, error, usb, wifi ?? [], "p30", "HUAWEI P30", streamingFor));

    [Fact]
    public void PhoneOnTheCableButNotStreaming()
    {
        // The case the old single status hid: "No phone connected" while the phone sat on the cable.
        var s = Status(Stats(ReceiverState.Connecting), usb: UsbPresence.Ready);
        Assert.Equal("Phone on the USB cable", s.Phone.Title);
        Assert.Equal(StatusTone.Good, s.Phone.Tone);
        Assert.Equal("Not streaming", s.Stream.Title);
        Assert.Contains("Start streaming", s.Stream.Detail);
        Assert.Equal(StatusTone.Attention, s.Stream.Tone);
    }

    [Fact]
    public void LiveOverUsb()
    {
        var s = Status(Stats(ReceiverState.Streaming, StreamEndpoint.Usb(), withStream: true, withDevice: true, rtt: 3.2),
            usb: UsbPresence.Ready, streamingFor: TimeSpan.FromSeconds(123));
        Assert.Equal("HUAWEI P30, connected over USB", s.Phone.Title);
        Assert.Equal("USB cable · round trip 3 ms · 40 °C", s.Phone.Detail);
        Assert.Equal("Live", s.Stream.Title);
        Assert.Equal("1920 × 1080 at 30 fps · H.264 · for 2:03", s.Stream.Detail);
        Assert.Equal(StatusTone.Good, s.Stream.Tone);
    }

    [Fact]
    public void ConnectedButNoPictureYet()
    {
        var s = Status(Stats(ReceiverState.Connected, StreamEndpoint.Wifi("192.168.1.22"), withDevice: true));
        Assert.Equal("HUAWEI P30, connected over Wi-Fi", s.Phone.Title);
        Assert.StartsWith("Wi-Fi 192.168.1.22", s.Phone.Detail);
        Assert.Equal("Starting", s.Stream.Title);
        Assert.Equal(StatusTone.Busy, s.Stream.Tone);
    }

    [Fact]
    public void OffWhenNotLooking()
    {
        var s = Status(looking: false);
        Assert.Equal("Off", s.Stream.Title);
        Assert.Equal("No phone found", s.Phone.Title);
        Assert.Equal("AirLens isn't looking for one.", s.Phone.Detail);
    }

    [Fact]
    public void UsbDebuggingNotAllowedYet()
    {
        var s = Status(Stats(ReceiverState.Connecting), usb: UsbPresence.Unauthorized);
        Assert.Equal("Phone on the USB cable", s.Phone.Title);
        Assert.Contains("accept the prompt", s.Phone.Detail);
        Assert.Equal(StatusTone.Attention, s.Phone.Tone);
        Assert.Equal("Not streaming", s.Stream.Title);
    }

    [Fact]
    public void NothingFound()
    {
        var s = Status(Stats(ReceiverState.Connecting));
        Assert.Equal("No phone found", s.Phone.Title);
        Assert.Equal("Waiting for your phone", s.Stream.Title);
        Assert.Contains("platform tools", Status(Stats(ReceiverState.Connecting), usb: UsbPresence.NoAdb).Phone.Detail);
    }

    [Fact]
    public void PhoneStreamingToAnotherPc()
    {
        var busy = new DiscoveredPhone("p30", "HUAWEI P30", IPAddress.Parse("192.168.1.22"), 47474, Busy: true);
        var s = Status(Stats(ReceiverState.Connecting), wifi: [busy]);
        Assert.Equal("HUAWEI P30 found on Wi-Fi", s.Phone.Title);
        Assert.Equal("Not streaming here", s.Stream.Title);
    }

    [Fact]
    public void StartErrorWins()
    {
        var s = Status(error: "FFmpeg DLLs not found");
        Assert.Equal("Can't start", s.Stream.Title);
        Assert.Equal("FFmpeg DLLs not found", s.Stream.Detail);
    }
}
