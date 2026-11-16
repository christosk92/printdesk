using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PrintDesk.Ipp;

/// <summary>A printer found on the network (or typed in).</summary>
sealed record PrinterRef(string Name, string Uri)
{
    public string Host => System.Uri.TryCreate(Uri.Replace("ipp://", "http://").Replace("ipps://", "https://"), UriKind.Absolute, out var u) ? u.Host : Uri;

    /// <summary>A typed address ("192.168.1.10", "printer.local", or a full ipp:// URI) to a printer URI.</summary>
    public static PrinterRef FromAddress(string address)
    {
        var a = address.Trim();
        if (!a.Contains("://", StringComparison.Ordinal)) a = $"ipp://{a}";
        var u = new Uri(a.Replace("ipp://", "http://").Replace("ipps://", "https://"));
        string path = u.AbsolutePath is "/" or "" ? "/ipp/print" : u.AbsolutePath;
        int port = u.IsDefaultPort ? 631 : u.Port;
        return new PrinterRef(u.Host, $"ipp://{u.Host}:{port}{path}");
    }
}

/// <summary>DNS-SD over multicast DNS: asks the local network for _ipp._tcp printers (what AirPrint/Mopria use).</summary>
static class Discovery
{
    private const string Service = "_ipp._tcp.local";
    private static readonly IPEndPoint s_group = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<IReadOnlyList<PrinterRef>> FindAsync(TimeSpan window, CancellationToken ct = default)
    {
        var instances = new Dictionary<string, (string? Target, int Port, string? Rp, string? Ty)>(StringComparer.OrdinalIgnoreCase);
        var addresses = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        var query = BuildQuery();

        // One socket per IPv4 interface: Windows sends multicast out of a single default interface otherwise.
        var sockets = new List<UdpClient>();
        foreach (var ip in LocalIPv4())
        {
            try
            {
                var udp = new UdpClient(new IPEndPoint(ip, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                sockets.Add(udp);
            }
            catch (SocketException) { }
        }
        if (sockets.Count == 0) return [];

        try
        {
            foreach (var s in sockets)
            {
                try { await s.SendAsync(query, s_group, ct).ConfigureAwait(false); } catch (SocketException) { }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(window);
            var receives = sockets.Select(s => ReceiveLoop(s, cts.Token, instances, addresses)).ToArray();
            await Task.WhenAll(receives).ConfigureAwait(false);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }

        var found = new List<PrinterRef>();
        foreach (var (instance, info) in instances)
        {
            if (info.Target is null) continue;
            string host = addresses.TryGetValue(info.Target, out var addr) ? addr.ToString() : info.Target.TrimEnd('.');
            string rp = string.IsNullOrEmpty(info.Rp) ? "ipp/print" : info.Rp!.TrimStart('/');
            string name = info.Ty ?? instance.Split("._ipp", 2)[0];
            found.Add(new PrinterRef(name, $"ipp://{host}:{(info.Port > 0 ? info.Port : 631)}/{rp}"));
        }
        return found.DistinctBy(p => p.Uri).OrderBy(p => p.Name).ToList();
    }

    private static async Task ReceiveLoop(UdpClient s, CancellationToken ct,
        Dictionary<string, (string? Target, int Port, string? Rp, string? Ty)> instances, Dictionary<string, IPAddress> addresses)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await s.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            lock (instances)
            {
                try { Parse(res.Buffer, instances, addresses); }
                catch (IndexOutOfRangeException) { }
                catch (ArgumentOutOfRangeException) { }
            }
        }
    }

    private static IEnumerable<IPAddress> LocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!ni.SupportsMulticast) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork) yield return ua.Address;
        }
    }

    private static byte[] BuildQuery()
    {
        var b = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };   // id 0, flags 0, 1 question
        foreach (var label in Service.Split('.'))
        {
            b.Add((byte)label.Length);
            b.AddRange(Encoding.ASCII.GetBytes(label));
        }
        b.Add(0);
        b.AddRange(new byte[] { 0, 12, 0, 1 });                         // PTR, class IN
        return b.ToArray();
    }

    private static void Parse(byte[] m, Dictionary<string, (string? Target, int Port, string? Rp, string? Ty)> instances,
        Dictionary<string, IPAddress> addresses)
    {
        if (m.Length < 12) return;
        int qd = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(4));
        int records = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(8))
                      + BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(10));
        int i = 12;
        for (int q = 0; q < qd; q++) { ReadName(m, ref i); i += 4; }
        for (int r = 0; r < records && i < m.Length; r++)
        {
            string name = ReadName(m, ref i);
            int type = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(i));
            int len = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(i + 8));
            int data = i + 10;
            i = data + len;
            switch (type)
            {
                case 12 when name.StartsWith("_ipp._tcp", StringComparison.OrdinalIgnoreCase):   // PTR
                {
                    int p = data;
                    string instance = ReadName(m, ref p);
                    if (!instances.ContainsKey(instance)) instances[instance] = (null, 0, null, null);
                    break;
                }
                case 33:                                                                         // SRV
                {
                    int port = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(data + 4));
                    int p = data + 6;
                    string target = ReadName(m, ref p);
                    instances.TryGetValue(name, out var cur);
                    instances[name] = (target, port, cur.Rp, cur.Ty);
                    break;
                }
                case 16:                                                                         // TXT
                {
                    string? rp = null, ty = null;
                    for (int p = data; p < data + len;)
                    {
                        int l = m[p++];
                        var kv = Encoding.UTF8.GetString(m, p, l);
                        p += l;
                        if (kv.StartsWith("rp=", StringComparison.OrdinalIgnoreCase)) rp = kv[3..];
                        else if (kv.StartsWith("ty=", StringComparison.OrdinalIgnoreCase)) ty = kv[3..];
                    }
                    instances.TryGetValue(name, out var cur);
                    instances[name] = (cur.Target, cur.Port, rp ?? cur.Rp, ty ?? cur.Ty);
                    break;
                }
                case 1 when len == 4:                                                            // A
                    addresses[name] = new IPAddress(m.AsSpan(data, 4));
                    break;
            }
        }
    }

    private static string ReadName(byte[] m, ref int i)
    {
        var sb = new StringBuilder();
        int p = i, hops = 0;
        bool jumped = false;
        while (p < m.Length && hops < 32)
        {
            int l = m[p];
            if (l == 0) { p++; break; }
            if ((l & 0xC0) == 0xC0)
            {
                int ptr = ((l & 0x3F) << 8) | m[p + 1];
                if (!jumped) i = p + 2;
                jumped = true;
                p = ptr;
                hops++;
                continue;
            }
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(m, p + 1, l));
            p += l + 1;
        }
        if (!jumped) i = p;
        return sb.ToString();
    }
}
