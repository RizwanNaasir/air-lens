using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AirLens.Core.Protocol;

namespace AirLens.Core.Transport;

/// <summary>A phone that answered discovery. <see cref="Address"/> is where its reply came from.</summary>
public sealed record DiscoveredPhone(string Id, string Name, IPAddress Address, int Port, bool Busy);

/// <summary>
/// Finds phones running AirLens on the local network, so nobody types an IP address
/// (docs/PROTOCOL.md, "Discovery"): one UDP query per LAN adapter to its subnet broadcast and to
/// 255.255.255.255, and the phones' unicast replies. Windows lets those replies through its
/// firewall for a few seconds after a broadcast, so no firewall rule is needed.
/// </summary>
public static class PhoneDiscovery
{
    public const int HeaderSize = 8;
    public const byte TypeQuery = 0x01;
    public const byte TypeAnnounce = 0x02;

    public static byte[] QueryPacket()
    {
        var p = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(p, ProtocolConstants.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), ProtocolConstants.Version);
        p[6] = TypeQuery;
        return p;
    }

    /// <summary>Null for anything that is not a well-formed announce.</summary>
    public static DiscoveredPhone? ParseAnnounce(ReadOnlySpan<byte> datagram, IPAddress from)
    {
        if (datagram.Length <= HeaderSize) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(datagram) != ProtocolConstants.Magic) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(datagram[4..]) != ProtocolConstants.Version || datagram[6] != TypeAnnounce) return null;
        try
        {
            var reader = new Utf8JsonReader(datagram[HeaderSize..]);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            if (string.IsNullOrEmpty(id)) return null;
            string name = root.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "Phone" : "Phone";
            int port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out int pv) ? pv : ProtocolConstants.DefaultPort;
            bool busy = root.TryGetProperty("busy", out var b) && b.ValueKind == JsonValueKind.True;
            return new DiscoveredPhone(id, name, from, port, busy);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Asks every LAN adapter's subnet; returns the phones that answered within <paramref name="timeout"/>.</summary>
    public static Task<IReadOnlyList<DiscoveredPhone>> ScanAsync(TimeSpan timeout, int port = ProtocolConstants.DefaultPort, CancellationToken ct = default) =>
        ScanAsync(
            LanRouting.BroadcastTargets().SelectMany(t => new[]
            {
                (t.Local, new IPEndPoint(t.Broadcast, port)),
                (t.Local, new IPEndPoint(IPAddress.Broadcast, port)),
            }),
            timeout,
            ct);

    /// <summary>Sends the query to each target from a socket bound to its local address (tests use loopback).</summary>
    public static async Task<IReadOnlyList<DiscoveredPhone>> ScanAsync(
        IEnumerable<(IPAddress Local, IPEndPoint Target)> targets, TimeSpan timeout, CancellationToken ct = default)
    {
        var query = QueryPacket();
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var group in targets.GroupBy(t => t.Local))
            {
                UdpClient udp;
                try
                {
                    udp = new UdpClient(new IPEndPoint(group.Key, 0)) { EnableBroadcast = true };
                }
                catch (SocketException)
                {
                    continue; // the adapter went away between listing and binding
                }
                sockets.Add(udp);
                foreach (var (_, target) in group)
                {
                    try
                    {
                        await udp.SendAsync(query, target, ct).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                    }
                }
            }

            var found = new Dictionary<string, DiscoveredPhone>();
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(timeout);
            await Task.WhenAll(sockets.Select(async udp =>
            {
                try
                {
                    while (true)
                    {
                        var r = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                        if (ParseAnnounce(r.Buffer, r.RemoteEndPoint.Address) is { } phone)
                            lock (found) found[phone.Id] = phone;
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (SocketException)
                {
                }
            })).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return found.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
    }
}
