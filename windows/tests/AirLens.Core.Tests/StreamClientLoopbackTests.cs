using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AirLens.Core.Buffering;
using AirLens.Core.Protocol;
using AirLens.Core.Transport;
using Xunit;

namespace AirLens.Core.Tests;

/// <summary>Drives the real <see cref="StreamClient"/> against a fake phone on loopback.</summary>
public class StreamClientLoopbackTests
{
    private sealed class Recorder : IStreamClientListener
    {
        public readonly BlockingCollection<string> Events = new();
        public readonly ConcurrentQueue<PacketBuffer> Frames = new();
        public void OnConnected(StreamEndpoint endpoint) => Events.Add("connected");
        public void OnDisconnected(StreamEndpoint endpoint, string reason) => Events.Add("disconnected");
        public void OnStreamInfo(StreamInfo info) => Events.Add($"info:{info.Width}x{info.Height}:{info.Epoch}");
        public void OnFrame(PacketBuffer frame)
        {
            Frames.Enqueue(frame);
            Events.Add($"frame:{frame.PtsUs}:{(frame.IsKeyframe ? "K" : "P")}");
        }
        public void OnRoundTrip(double rttMs) { }
        public void OnStatus(string message) { }
        public void OnDeviceState(DeviceState state) => Events.Add($"device:{state.Device}:{state.LensKey}");

        public string Next(int timeoutMs = 5000) =>
            Events.TryTake(out var e, timeoutMs) ? e : throw new TimeoutException("no event");

        public string NextMatching(string prefix, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var e = Next((int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds));
                if (e.StartsWith(prefix)) return e;
            }
            throw new TimeoutException(prefix);
        }
    }

    private static void Send(NetworkStream s, PacketType type, byte flags, ReadOnlySpan<byte> payload, long ts = 0)
    {
        var buf = new byte[ProtocolConstants.HeaderSize + payload.Length];
        new PacketHeader(type, flags, payload.Length, 0, ts).Write(buf);
        payload.CopyTo(buf.AsSpan(ProtocolConstants.HeaderSize));
        s.Write(buf);
    }

    private static byte[] Info(uint epoch)
    {
        var p = new byte[ProtocolConstants.StreamInfoSize];
        p[0] = (byte)VideoCodec.H264;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 3840);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), 2160);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), 30);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), epoch);
        return p;
    }

    private static byte[] ReadHello(NetworkStream s)
    {
        var hello = new byte[ClientPackets.HelloLength];
        s.ReadExactly(hello);
        return hello;
    }

    [Fact]
    public void ReceivesInfoAndFramesThenReconnectsAfterServerDrop()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var pool = new PacketBufferPool();
        var rec = new Recorder();
        using var client = new StreamClient([StreamEndpoint.Wifi("127.0.0.1", port)], pool, rec);
        client.Start();

        // Session 1: handshake, info, two frames, then the phone vanishes.
        using (var phone = listener.AcceptTcpClient())
        {
            var s = phone.GetStream();
            var hello = ReadHello(s);
            Assert.Equal((byte)PacketType.Hello, hello[0]);
            Assert.Equal(ProtocolConstants.Magic, BinaryPrimitives.ReadUInt32BigEndian(hello.AsSpan(20)));
            Assert.Equal("connected", rec.Next());
            Send(s, PacketType.StreamInfo, 0, Info(1));
            Send(s, PacketType.VideoFrame, (byte)FrameFlags.Keyframe, new byte[300_000], ts: 100);
            Send(s, PacketType.Heartbeat, 0, ReadOnlySpan<byte>.Empty);
            Send(s, PacketType.VideoFrame, 0, new byte[1000], ts: 200);
            Assert.Equal("info:3840x2160:1", rec.Next());
            Assert.Equal("frame:100:K", rec.Next());
            Assert.Equal("frame:200:P", rec.Next());
        }
        Assert.Equal("disconnected", rec.NextMatching("disconnected"));

        // Session 2: the client reconnects by itself and frames carry the new epoch.
        using (var phone = listener.AcceptTcpClient())
        {
            var s = phone.GetStream();
            ReadHello(s);
            Assert.Equal("connected", rec.NextMatching("connected"));
            Send(s, PacketType.StreamInfo, 0, Info(2));
            Send(s, PacketType.VideoFrame, (byte)FrameFlags.Keyframe, new byte[10], ts: 300);
            Assert.Equal("info:3840x2160:2", rec.NextMatching("info"));
            Assert.Equal("frame:300:K", rec.NextMatching("frame"));
        }

        var frames = rec.Frames.ToArray();
        Assert.Equal(new uint[] { 1, 1, 2 }, frames.Select(f => f.Epoch));
        Assert.Equal(300_000, frames[0].Length);
        foreach (var f in frames) f.Return();
        Assert.Equal(0, pool.Outstanding);
        listener.Stop();
    }

    [Fact]
    public void SendsPingsSoThePhoneKeepsTheSession()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var rec = new Recorder();
        using var client = new StreamClient([StreamEndpoint.Wifi("127.0.0.1", port)], new PacketBufferPool(), rec);
        client.Start();
        using var phone = listener.AcceptTcpClient();
        var s = phone.GetStream();
        ReadHello(s);
        var header = new byte[ProtocolConstants.HeaderSize];
        s.ReadTimeout = 2000;
        s.ReadExactly(header);
        Assert.Equal((byte)PacketType.Ping, header[0]);
        listener.Stop();
    }

    [Fact]
    public void DeviceStateArrivesAndControlGoesToThePhone()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var rec = new Recorder();
        using var client = new StreamClient([StreamEndpoint.Wifi("127.0.0.1", port)], new PacketBufferPool(), rec);
        client.Start();
        using var phone = listener.AcceptTcpClient();
        var s = phone.GetStream();
        ReadHello(s);
        Assert.Equal("connected", rec.Next());
        Send(s, PacketType.StreamInfo, 0, Info(1));
        Send(s, PacketType.DeviceState, 0, """{"device":"Test phone","lens":"2","lenses":[],"presets":[]}"""u8);
        Assert.Equal("device:Test phone:2", rec.NextMatching("device"));

        client.SendControl(ClientPackets.BuildControl("3", null));
        s.ReadTimeout = 2000;
        var header = new byte[ProtocolConstants.HeaderSize];
        while (true) // pings are interleaved
        {
            s.ReadExactly(header);
            var h = PacketHeader.Read(header);
            var payload = new byte[h.PayloadLength];
            s.ReadExactly(payload);
            if (h.Type != PacketType.Control) continue;
            var text = System.Text.Encoding.UTF8.GetString(payload);
            if (text.Contains("virtualCamera")) continue; // sent on every connect
            Assert.Equal("""{"lens":"3"}""", text);
            break;
        }
        listener.Stop();
    }

    /// <summary>The cable, as the client sees it through ADB: attached or not.</summary>
    private sealed class FakeUsb : IUsbLink
    {
        public volatile bool Attached;
        public int Forwards;

        public AdbResult Forward(int localPort, int remotePort)
        {
            Interlocked.Increment(ref Forwards);
            return Attached ? new(AdbStatus.Ok, "forwarded") : new(AdbStatus.NoDevice, "No phone found.");
        }

        public bool PhoneAttached() => Attached;
    }

    private static TcpClient Accept(TcpListener listener)
    {
        var accept = listener.AcceptTcpClientAsync();
        Assert.True(accept.Wait(5000), "the client never connected");
        return accept.Result;
    }

    [Fact]
    public void GoesBackToUsbWhenTheCableReturns()
    {
        var usbPhone = new TcpListener(IPAddress.Loopback, 0);
        var wifiPhone = new TcpListener(IPAddress.Loopback, 0);
        usbPhone.Start();
        wifiPhone.Start();
        var usb = new FakeUsb();
        var rec = new Recorder();
        StreamEndpoint[] endpoints =
        [
            new(EndpointKind.Usb, "127.0.0.1", ((IPEndPoint)usbPhone.LocalEndpoint).Port),
            StreamEndpoint.Wifi("127.0.0.1", ((IPEndPoint)wifiPhone.LocalEndpoint).Port),
        ];
        using var client = new StreamClient(endpoints, new PacketBufferPool(), rec, usb, TimeSpan.FromMilliseconds(100));
        client.Start();

        // No cable: the client settles on Wi-Fi.
        using var wifi = Accept(wifiPhone);
        var ws = wifi.GetStream();
        ReadHello(ws);
        Assert.Equal("connected", rec.NextMatching("connected"));
        Send(ws, PacketType.StreamInfo, 0, Info(1));
        Send(ws, PacketType.VideoFrame, (byte)FrameFlags.Keyframe, new byte[10], ts: 100);
        Assert.Equal("frame:100:K", rec.NextMatching("frame"));
        Assert.Equal(EndpointKind.Wifi, client.ConnectedEndpoint?.Kind);

        // Cable back: the client opens USB next to Wi-Fi and does the handshake there first.
        usb.Attached = true;
        using var cable = Accept(usbPhone);
        var us = cable.GetStream();
        ReadHello(us);
        Send(us, PacketType.StreamInfo, 0, Info(1));
        Send(us, PacketType.VideoFrame, (byte)FrameFlags.Keyframe, new byte[10], ts: 200);
        Assert.Equal("disconnected", rec.NextMatching("disconnected"));
        Assert.Equal("connected", rec.NextMatching("connected"));
        Assert.Equal("info:3840x2160:1", rec.NextMatching("info"));
        Assert.Equal("frame:200:K", rec.NextMatching("frame"));
        Assert.Equal(EndpointKind.Usb, client.ConnectedEndpoint?.Kind);

        // It let go of Wi-Fi itself; it did not wait for the phone to drop it.
        ws.ReadTimeout = 3000;
        var sink = new byte[256];
        while (ws.Read(sink) > 0) { } // pings until the close
        usbPhone.Stop();
        wifiPhone.Stop();
    }

    [Fact]
    public void StaysOnWifiWhenUsbDoesNotAnswer()
    {
        var gone = new TcpListener(IPAddress.Loopback, 0);
        gone.Start();
        int deadPort = ((IPEndPoint)gone.LocalEndpoint).Port;
        gone.Stop(); // a phone on the cable that is not running AirLens: connects are refused
        var wifiPhone = new TcpListener(IPAddress.Loopback, 0);
        wifiPhone.Start();
        var usb = new FakeUsb { Attached = true };
        var rec = new Recorder();
        StreamEndpoint[] endpoints =
        [
            new(EndpointKind.Usb, "127.0.0.1", deadPort),
            StreamEndpoint.Wifi("127.0.0.1", ((IPEndPoint)wifiPhone.LocalEndpoint).Port),
        ];
        using var client = new StreamClient(endpoints, new PacketBufferPool(), rec, usb, TimeSpan.FromMilliseconds(100));
        client.Start();

        using var wifi = Accept(wifiPhone);
        var ws = wifi.GetStream();
        ReadHello(ws);
        Assert.Equal("connected", rec.NextMatching("connected"));
        Thread.Sleep(1000); // room for ten checks
        Send(ws, PacketType.StreamInfo, 0, Info(1));
        Send(ws, PacketType.VideoFrame, (byte)FrameFlags.Keyframe, new byte[10], ts: 300);
        var seen = new List<string>();
        while (true)
        {
            var e = rec.Next();
            seen.Add(e);
            if (e == "frame:300:K") break;
        }
        Assert.DoesNotContain("disconnected", seen);
        Assert.Equal(EndpointKind.Wifi, client.ConnectedEndpoint?.Kind);
        Assert.InRange(usb.Forwards, 1, 2); // the first connect, one probe, then a 30 s rest
        wifiPhone.Stop();
    }

    [Fact]
    public void ProtocolViolationClosesSessionWithoutCrashing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var rec = new Recorder();
        using var client = new StreamClient([StreamEndpoint.Wifi("127.0.0.1", port)], new PacketBufferPool(), rec);
        client.Start();
        using (var phone = listener.AcceptTcpClient())
        {
            var s = phone.GetStream();
            ReadHello(s);
            var bad = new byte[ProtocolConstants.HeaderSize];
            new PacketHeader(PacketType.VideoFrame, 0, ProtocolConstants.MaxPayload + 1, 0, 0).Write(bad);
            s.Write(bad);
            Assert.Equal("disconnected", rec.NextMatching("disconnected"));
        }
        // And it comes back.
        using var again = listener.AcceptTcpClient();
        Assert.Equal("connected", rec.NextMatching("connected"));
        listener.Stop();
    }
}
