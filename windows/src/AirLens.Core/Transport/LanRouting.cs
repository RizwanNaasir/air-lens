using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirLens.Core.Transport;

/// <summary>
/// Keeps phone traffic on the local network even when a VPN claims the same subnet.
///
/// Found on the development PC: a Tailscale subnet route for 192.168.1.0/24 (a remote home LAN)
/// overlapped the local Wi-Fi's 192.168.1.0/24, so Windows sent the connection to the phone into the
/// tunnel and it timed out, although ping worked. Windows routes a socket that is bound to a local
/// address only through the interface that owns that address (strong-host send). So the client binds
/// to the address of the adapter whose subnet actually contains the phone.
/// </summary>
public static class LanRouting
{
    /// <summary>
    /// The local IPv4 address on an up, non-tunnel interface whose subnet contains
    /// <paramref name="target"/>; the most specific subnet wins, physical adapters first. Null when
    /// no adapter is on-link (then the OS routing table is used as usual).
    /// </summary>
    public static IPAddress? SourceAddressFor(IPAddress target)
    {
        if (target.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(target)) return null;
        IPAddress? best = null;
        int bestPrefix = -1;
        bool bestPhysical = false;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            bool physical = nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                int prefix = unicast.PrefixLength;
                // /32 host routes (VPN tunnel addresses such as Tailscale's 100.x.y.z/32) never put a peer on-link.
                if (address.AddressFamily != AddressFamily.InterNetwork || prefix <= 0 || prefix >= 32) continue;
                if (!SameSubnet(address, target, prefix)) continue;
                if (prefix > bestPrefix || (prefix == bestPrefix && physical && !bestPhysical))
                {
                    best = address;
                    bestPrefix = prefix;
                    bestPhysical = physical;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Every up, non-tunnel IPv4 adapter with a real subnet, as (its address, its subnet's broadcast
    /// address). Discovery sends one query per adapter, from that adapter, so a VPN cannot swallow it.
    /// </summary>
    public static IReadOnlyList<(IPAddress Local, IPAddress Broadcast)> BroadcastTargets()
    {
        var result = new List<(IPAddress, IPAddress)>();
        Span<byte> b = stackalloc byte[4]; // one buffer for the loop; new IPAddress(b) copies it
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                int prefix = unicast.PrefixLength;
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || prefix <= 0 || prefix > 30) continue;
                uint mask = uint.MaxValue << (32 - prefix);
                BinaryPrimitives.WriteUInt32BigEndian(b, ToUInt32(unicast.Address) | ~mask);
                result.Add((unicast.Address, new IPAddress(b)));
            }
        }
        return result;
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, int prefixLength)
    {
        if (prefixLength <= 0) return true;
        uint mask = prefixLength >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefixLength);
        return (ToUInt32(a) & mask) == (ToUInt32(b) & mask);
    }

    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    /// <summary>Resolves <paramref name="host"/> to its first IPv4 address (literal IPs need no DNS).</summary>
    public static IPAddress Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;
        return Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }
}
