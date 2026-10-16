using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AirLens.Core.Protocol;
using AirLens.Core.Transport;
using Xunit;

namespace AirLens.Core.Tests;

public class PhoneDiscoveryTests
{
    private static byte[] Announce(string json, byte type = PhoneDiscovery.TypeAnnounce)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var p = new byte[PhoneDiscovery.HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(p, ProtocolConstants.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), ProtocolConstants.Version);
        p[6] = type;
        body.CopyTo(p, PhoneDiscovery.HeaderSize);
        return p;
    }

    [Fact]
    public void QueryMatchesGoldenVector()
    {
        Assert.Equal(new byte[] { 0x41, 0x4C, 0x4E, 0x53, 0x00, 0x01, 0x01, 0x00 }, PhoneDiscovery.QueryPacket());
    }

    [Fact]
    public void AnnounceParsesAndTakesTheSenderAsAddress()
    {
        var from = IPAddress.Parse("192.168.1.72");
        var phone = PhoneDiscovery.ParseAnnounce(Announce("""{"id":"3f9a0c1d2b4e5f60","device":"HUAWEI P30","port":47474,"busy":true,"future":1}"""), from)!;
        Assert.Equal("3f9a0c1d2b4e5f60", phone.Id);
        Assert.Equal("HUAWEI P30", phone.Name);
        Assert.Equal(from, phone.Address);
        Assert.Equal(47474, phone.Port);
        Assert.True(phone.Busy);
    }

    [Fact]
    public void AnythingElseIsIgnored()
    {
        var from = IPAddress.Loopback;
        Assert.Null(PhoneDiscovery.ParseAnnounce(PhoneDiscovery.QueryPacket(), from));
        Assert.Null(PhoneDiscovery.ParseAnnounce(Announce("""{"id":"x"}""", type: PhoneDiscovery.TypeQuery), from));
        Assert.Null(PhoneDiscovery.ParseAnnounce(Announce("not json"), from));
        Assert.Null(PhoneDiscovery.ParseAnnounce(Announce("""{"device":"no id"}"""), from));
        Assert.Null(PhoneDiscovery.ParseAnnounce("hello"u8, from));
    }

    [Fact]
    public async Task ScanFindsAPhoneThatAnswers()
    {
        // A fake phone on loopback that answers the query the way the Android responder does.
        using var phone = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)phone.Client.LocalEndPoint!).Port;
        var answering = Task.Run(async () =>
        {
            var q = await phone.ReceiveAsync();
            if (q.Buffer.AsSpan().SequenceEqual(PhoneDiscovery.QueryPacket()))
                await phone.SendAsync(Announce("""{"id":"abc","device":"Test phone","port":47474,"busy":false}"""), q.RemoteEndPoint);
        });
        var found = await PhoneDiscovery.ScanAsync([(IPAddress.Loopback, new IPEndPoint(IPAddress.Loopback, port))], TimeSpan.FromSeconds(1));
        await answering;
        var p = Assert.Single(found);
        Assert.Equal("Test phone", p.Name);
        Assert.Equal(IPAddress.Loopback, p.Address);
    }

    [Fact]
    public void BroadcastTargetsAreSubnetBroadcasts()
    {
        foreach (var (local, broadcast) in LanRouting.BroadcastTargets())
        {
            Assert.Equal(AddressFamily.InterNetwork, local.AddressFamily);
            Assert.NotEqual(local, broadcast);
            Assert.Equal(255, broadcast.GetAddressBytes()[3] | 0xFE); // the host part's last bit is set
        }
    }
}
