using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RikRealization.ClassicCast.Protocol.Discovery;

/// <summary>
/// mDNS/DNS-SD browser for <c>_googlecast._tcp.local</c>, written against the wire format
/// directly so the library carries no dependencies.
/// </summary>
public sealed class CastDiscovery
{
    private const string ServiceName = "_googlecast._tcp.local";
    private const int MdnsPort = 5353;
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");

    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeAaaa = 28, TypeSrv = 33;

    /// <summary>
    /// Browses for Cast receivers until <paramref name="timeout"/> elapses.
    /// Records arrive spread across packets, so everything is accumulated and correlated
    /// at the end rather than assuming any one response is complete.
    /// </summary>
    public static async Task<IReadOnlyList<CastDevice>> DiscoverAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        var srv = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
        var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var hosts = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        using var socket = CreateSocket(out bool boundToMdnsPort);
        var localAddresses = LocalIPv4Addresses().ToList();

        if (boundToMdnsPort)
            JoinGroup(socket, localAddresses);

        var query = BuildPtrQuery(ServiceName, unicastResponse: !boundToMdnsPort);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var receiving = ReceiveLoopAsync(socket, srv, txt, hosts, cts.Token);

        // Multi-homed machines (VPNs, Docker, Tailscale) will not necessarily route
        // multicast out of the interface we care about, so query from each one in turn.
        for (int attempt = 0; attempt < 3 && !cts.IsCancellationRequested; attempt++)
        {
            foreach (var local in localAddresses)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP,
                        SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    await socket.SendToAsync(query, SocketFlags.None,
                        new IPEndPoint(MdnsGroup, MdnsPort), cts.Token);
                }
                catch (SocketException) { /* interface went away, or refuses multicast */ }
                catch (OperationCanceledException) { break; }
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(700), cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        await receiving;

        var devices = new List<CastDevice>();
        foreach (var (instance, service) in srv)
        {
            if (!txt.TryGetValue(instance, out var records)) continue;
            if (!hosts.TryGetValue(service.Target, out var address)) continue;

            devices.Add(new CastDevice
            {
                Id = records.GetValueOrDefault("id", instance),
                FriendlyName = records.GetValueOrDefault("fn", instance.Split('.')[0]),
                Model = records.GetValueOrDefault("md", "Unknown"),
                Address = address,
                Port = service.Port,
                InstanceName = instance,
                Txt = records,
            });
        }

        return devices
            .GroupBy(d => d.Id)
            .Select(g => g.First())
            .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Socket CreateSocket(out bool boundToMdnsPort)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

        // Binding 5353 lets us see multicast answers addressed to every listener, which is
        // by far the more reliable path. It can fail when Bonjour or another responder
        // holds the port exclusively, in which case we fall back to an ephemeral port and
        // ask responders for a unicast reply instead.
        try
        {
            socket.ExclusiveAddressUse = false;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            boundToMdnsPort = true;
        }
        catch (SocketException)
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            boundToMdnsPort = false;
        }

        return socket;
    }

    private static void JoinGroup(Socket socket, IEnumerable<IPAddress> locals)
    {
        foreach (var local in locals)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(MdnsGroup, local));
            }
            catch (SocketException) { /* already joined, or the interface cannot */ }
        }
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return ua.Address;
        }
    }

    private static async Task ReceiveLoopAsync(
        Socket socket,
        Dictionary<string, (string, int)> srv,
        Dictionary<string, Dictionary<string, string>> txt,
        Dictionary<string, IPAddress> hosts,
        CancellationToken ct)
    {
        var buffer = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            int received;
            try
            {
                var result = await socket.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                received = result.ReceivedBytes;
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }

            try
            {
                ParseResponse(buffer.AsSpan(0, received), srv, txt, hosts);
            }
            catch (ArgumentOutOfRangeException) { /* truncated or malformed packet */ }
            catch (IndexOutOfRangeException) { }
        }
    }

    private static void ParseResponse(
        ReadOnlySpan<byte> packet,
        Dictionary<string, (string, int)> srv,
        Dictionary<string, Dictionary<string, string>> txt,
        Dictionary<string, IPAddress> hosts)
    {
        var r = new DnsReader(packet);
        if (r.Remaining < 12) return;

        r.ReadUInt16();                     // transaction id, unused in mDNS
        ushort flags = r.ReadUInt16();
        if ((flags & 0x8000) == 0) return;  // a query, not a response

        ushort questionCount = r.ReadUInt16();
        int recordCount = r.ReadUInt16() + r.ReadUInt16() + r.ReadUInt16();

        for (int i = 0; i < questionCount; i++)
        {
            r.SkipName();
            r.ReadUInt16();                 // qtype
            r.ReadUInt16();                 // qclass
        }

        for (int i = 0; i < recordCount && !r.AtEnd; i++)
        {
            string name = r.ReadName();
            ushort type = r.ReadUInt16();
            r.ReadUInt16();                 // class, plus the cache-flush bit
            r.ReadUInt32();                 // ttl
            ushort rdLength = r.ReadUInt16();

            int rdStart = r.Position;

            // Bound to port 5353 we see every responder on the network — printers, Oculus
            // link services, other machines. Only records belonging to our service type
            // are ours to interpret. Host A records are exempt: their names are hostnames,
            // not service instances, and SRV targets point at them.
            bool ours = type == TypeA ||
                        name.EndsWith(ServiceName, StringComparison.OrdinalIgnoreCase);

            switch (type)
            {
                case TypeSrv when ours:
                {
                    r.ReadUInt16();         // priority
                    r.ReadUInt16();         // weight
                    ushort port = r.ReadUInt16();
                    string target = r.ReadName();
                    srv[name] = (target, port);
                    break;
                }

                case TypeTxt when ours:
                    txt[name] = ParseTxt(r.Slice(rdStart, rdLength));
                    break;

                case TypeA when rdLength == 4:
                    hosts[name] = new IPAddress(r.Slice(rdStart, 4).ToArray());
                    break;

                case TypePtr:
                case TypeAaaa:
                default:
                    break;
            }

            r.Position = rdStart + rdLength; // never trust the per-type reader to land right
        }
    }

    private static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> rdata)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < rdata.Length)
        {
            int len = rdata[i++];
            if (len == 0 || i + len > rdata.Length) break;

            string entry = Encoding.UTF8.GetString(rdata.Slice(i, len));
            i += len;

            int eq = entry.IndexOf('=');
            if (eq > 0) map[entry[..eq]] = entry[(eq + 1)..];
        }
        return map;
    }

    private static byte[] BuildPtrQuery(string service, bool unicastResponse)
    {
        var buf = new List<byte>(64);

        buf.AddRange(new byte[] { 0, 0 });               // id
        buf.AddRange(new byte[] { 0, 0 });               // flags: standard query
        buf.AddRange(new byte[] { 0, 1 });               // qdcount
        buf.AddRange(new byte[] { 0, 0, 0, 0, 0, 0 });   // ancount / nscount / arcount

        foreach (var label in service.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            buf.Add((byte)bytes.Length);
            buf.AddRange(bytes);
        }
        buf.Add(0);

        buf.AddRange(new byte[] { 0, (byte)TypePtr });

        // Top bit of QCLASS is the QU bit: "answer me directly". Only worth asking when we
        // could not bind the mDNS port and so will not see multicast answers.
        ushort qclass = unicastResponse ? (ushort)0x8001 : (ushort)0x0001;
        buf.Add((byte)(qclass >> 8));
        buf.Add((byte)(qclass & 0xFF));

        return buf.ToArray();
    }
}
