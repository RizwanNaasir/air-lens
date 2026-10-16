using System.Net;
using AirLens.Core.Transport;
using Xunit;

namespace AirLens.Core.Tests;

public class LanRoutingTests
{
    [Theory]
    [InlineData("192.168.1.27", "192.168.1.14", 24, true)]
    [InlineData("192.168.1.27", "192.168.2.14", 24, false)]
    [InlineData("10.0.5.1", "10.0.200.9", 16, true)]
    [InlineData("100.66.30.48", "192.168.1.14", 32, false)] // a VPN /32 never contains the phone
    [InlineData("172.30.16.1", "172.30.31.255", 20, true)]
    [InlineData("172.30.16.1", "172.30.32.1", 20, false)]
    public void SameSubnet(string local, string target, int prefix, bool expected)
    {
        Assert.Equal(expected, LanRouting.SameSubnet(IPAddress.Parse(local), IPAddress.Parse(target), prefix));
    }

    [Fact]
    public void LoopbackAndIpv6NeedNoBinding()
    {
        Assert.Null(LanRouting.SourceAddressFor(IPAddress.Loopback));
        Assert.Null(LanRouting.SourceAddressFor(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void ResolvesLiteralAddressesWithoutDns()
    {
        Assert.Equal(IPAddress.Parse("192.168.1.14"), LanRouting.Resolve("192.168.1.14"));
    }
}
