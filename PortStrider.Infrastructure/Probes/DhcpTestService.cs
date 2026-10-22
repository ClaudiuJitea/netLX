using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.Infrastructure.Probes;

/// <summary>Tests DHCP without taking over the operating system's existing address.</summary>
public sealed class DhcpTestService : IDhcpTestService
{
    private const int OfferTimeoutMs = 8000;
    private const int AckTimeoutMs = 5000;
    private readonly Func<AdapterInfo, IDhcpTransport> _openTransport;

    public DhcpTestService() : this(DhcpTransport.Open) { }
    internal DhcpTestService(Func<AdapterInfo, IDhcpTransport> openTransport) => _openTransport = openTransport;

    // allowRenew is retained for API compatibility; testing never launches a second OS DHCP client.
    public async Task<DhcpTestResult> DiscoverAsync(AdapterInfo adapter, TestProfile profile, bool allowRenew, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mac = DhcpPacket.ParseMac(adapter.MacAddress);
        if (mac.Length != 6) return Unsupported("Adapter MAC address unavailable.");
        try
        {
            using var transport = _openTransport(adapter);
            // An existing address belongs to the OS. INFORM requests current DHCP configuration without
            // creating/replacing a lease, even if the OS uses a DUID rather than a MAC client identifier.
            if (UsableAddress(adapter.Ipv4))
                return await InformAsync(transport, adapter, profile, mac, cancellationToken);
            return await DoraAsync(transport, adapter, profile, mac, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Unsupported(Short(ex.Message)); }
    }

    internal static bool UsableAddress(string? address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
        && ip.GetAddressBytes()[0] is > 0 and < 224 && !IPAddress.IsLoopback(ip)
        && !(ip.GetAddressBytes()[0] == 169 && ip.GetAddressBytes()[1] == 254);

    private static async Task<DhcpTestResult> InformAsync(IDhcpTransport transport, AdapterInfo adapter,
        TestProfile profile, byte[] mac, CancellationToken cancellationToken)
    {
        var xid = Random.Shared.Next();
        var sw = Stopwatch.StartNew();
        var packet = DhcpPacket.Build(DhcpPacket.MessageType.Inform, mac, xid, profile, clientIp: adapter.Ipv4);
        await transport.SendAsync(packet, IPAddress.Parse(adapter.Ipv4!), cancellationToken);
        var ack = await WaitForAsync(transport, xid, mac, DhcpPacket.MessageType.Ack, AckTimeoutMs, cancellationToken);
        var opts = ack?.Options;
        return new DhcpTestResult
        {
            Supported = true,
            Status = ack is null ? TestStatus.Warning : TestStatus.Pass,
            Summary = ack is null
                ? $"Existing address {adapter.Ipv4} retained · no DHCP INFORM Ack within {AckTimeoutMs / 1000}s; server response not verified"
                : $"DHCP INFORM Ack in {sw.ElapsedMilliseconds} ms from {ack.Options.GetValueOrDefault(54) ?? ack.Remote.Address.ToString()} · existing address {adapter.Ipv4} retained (lease acquisition not tested)",
            InformSent = true,
            ExistingIp = adapter.Ipv4,
            AckReceived = ack is not null,
            AckMs = ack is null ? 0 : sw.ElapsedMilliseconds,
            ServerIp = opts?.GetValueOrDefault(54) ?? ack?.Remote.Address.ToString(),
            Subnet = opts?.GetValueOrDefault(1),
            Gateway = opts?.GetValueOrDefault(3),
            Dns = opts?.GetValueOrDefault(6),
            DomainName = opts?.GetValueOrDefault(15),
            Option43 = opts?.GetValueOrDefault(43),
            Option60 = opts?.GetValueOrDefault(60),
            Option150 = opts?.GetValueOrDefault(150),
            InterfaceName = adapter.Name
        };
    }

    private static async Task<DhcpTestResult> DoraAsync(IDhcpTransport transport, AdapterInfo adapter,
        TestProfile profile, byte[] mac, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var xid = Random.Shared.Next();
        var discover = DhcpPacket.Build(DhcpPacket.MessageType.Discover, mac, xid, profile);
        await transport.SendAsync(discover, IPAddress.Any, cancellationToken);
        var discoverMs = sw.ElapsedMilliseconds;
        var offer = await WaitForAsync(transport, xid, mac, DhcpPacket.MessageType.Offer, OfferTimeoutMs, cancellationToken);
        if (offer is null)
            return new DhcpTestResult
            {
                Supported = true, Status = TestStatus.Fail,
                Summary = $"Discover sent, no Offer within {OfferTimeoutMs / 1000}s",
                DiscoverMs = discoverMs, InterfaceName = adapter.Name
            };

        var offerMs = sw.ElapsedMilliseconds - discoverMs;
        var serverIp = offer.Options[54];
        var requestStart = sw.ElapsedMilliseconds;
        var request = DhcpPacket.Build(DhcpPacket.MessageType.Request, mac, xid, profile, offer.YourIp, serverIp);
        await transport.SendAsync(request, IPAddress.Any, cancellationToken);
        var ack = await WaitForAsync(transport, xid, mac, DhcpPacket.MessageType.Ack, AckTimeoutMs,
            cancellationToken, DhcpPacket.MessageType.Nak, serverIp, offer.YourIp);
        var ackMs = ack is null ? 0 : sw.ElapsedMilliseconds - requestStart;
        var final = ack is { Type: DhcpPacket.MessageType.Ack } ? ack : offer;
        var opts = final.Options;
        var leaseSeconds = opts.TryGetValue(51, out var lease) && long.TryParse(lease, out var secs) ? secs : 0;
        return new DhcpTestResult
        {
            Supported = true,
            Status = ack switch
            {
                { Type: DhcpPacket.MessageType.Ack } => TestStatus.Pass,
                { Type: DhcpPacket.MessageType.Nak } => TestStatus.Fail,
                _ => TestStatus.Warning
            },
            Summary = ack switch
            {
                { Type: DhcpPacket.MessageType.Ack } => $"Offer {offer.YourIp} in {offerMs} ms · Ack in {ackMs} ms from {serverIp}",
                { Type: DhcpPacket.MessageType.Nak } => $"Offer {offer.YourIp} in {offerMs} ms · server {serverIp} NAKed the request",
                _ => $"Offer {offer.YourIp} in {offerMs} ms · no Ack within {AckTimeoutMs / 1000}s"
            },
            DiscoverMs = discoverMs, OfferMs = offerMs, RequestMs = requestStart - discoverMs, AckMs = ackMs,
            RequestSent = true, AckReceived = ack is { Type: DhcpPacket.MessageType.Ack },
            ServerIp = serverIp, OfferedIp = final.YourIp,
            Subnet = opts.GetValueOrDefault(1), Gateway = opts.GetValueOrDefault(3), Dns = opts.GetValueOrDefault(6),
            DomainName = opts.GetValueOrDefault(15), Option43 = opts.GetValueOrDefault(43),
            Option60 = opts.GetValueOrDefault(60), Option150 = opts.GetValueOrDefault(150),
            LeaseTime = leaseSeconds > 0 ? FormatLease(leaseSeconds) : null, InterfaceName = adapter.Name
        };
    }

    private static async Task<DhcpPacket.Reply?> WaitForAsync(IDhcpTransport transport, int xid, byte[] mac,
        DhcpPacket.MessageType wanted, int timeoutMs, CancellationToken cancellationToken,
        DhcpPacket.MessageType? alsoAccept = null, string? serverIp = null, string? offeredIp = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);
        try
        {
            while (true)
            {
                var result = await transport.ReceiveAsync(cts.Token);
                var reply = DhcpPacket.TryParseReply(result.Buffer, result.RemoteEndPoint, xid, mac);
                if (reply is null || (reply.Type != wanted && reply.Type != alsoAccept)) continue;
                if (!reply.Options.TryGetValue(54, out var server) || !UsableAddress(server)) continue;
                if (serverIp is not null && server != serverIp) continue;
                if (wanted == DhcpPacket.MessageType.Offer && !UsableAddress(reply.YourIp)) continue;
                if (offeredIp is not null && reply.Type == DhcpPacket.MessageType.Ack && reply.YourIp != offeredIp) continue;
                return reply;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private static DhcpTestResult Unsupported(string summary) => new()
    {
        Supported = false, Status = TestStatus.Skipped, Summary = summary
    };

    internal static string FormatLease(long seconds)
    {
        if (seconds <= 0) return "—";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return ts.Hours > 0 ? $"{(int)ts.TotalDays}d {ts.Hours}h" : $"{(int)ts.TotalDays}d";
        if (ts.TotalHours >= 1) return ts.Minutes > 0 ? $"{(int)ts.TotalHours}h {ts.Minutes}m" : $"{(int)ts.TotalHours}h";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return $"{seconds}s";
    }

    private static string Short(string message) => message.Length <= 120 ? message : message[..117] + "…";
}

/// <summary>DHCPv4 wire encoding/decoding (RFC 2131/2132) for the active test.</summary>
internal static class DhcpPacket
{
    public enum MessageType : byte
    {
        Discover = 1,
        Offer = 2,
        Request = 3,
        Decline = 4,
        Ack = 5,
        Nak = 6,
        Release = 7,
        Inform = 8
    }

    public sealed class Reply
    {
        public required MessageType Type { get; init; }
        public required string YourIp { get; init; }
        public required IPEndPoint Remote { get; init; }
        public required IReadOnlyDictionary<int, string> Options { get; init; }
    }

    private static readonly byte[] BaseParameterRequest = [1, 3, 6, 12, 15, 28, 42, 51, 54, 58, 59];

    public static byte[] Build(MessageType type, byte[] mac, int xid, TestProfile profile, string? requestedIp = null, string? serverId = null, string? clientIp = null)
    {
        var packet = new byte[576];
        packet[0] = 1; // BOOTREQUEST
        packet[1] = 1; // Ethernet
        packet[2] = 6; // hlen
        packet[4] = (byte)(xid >> 24);
        packet[5] = (byte)(xid >> 16);
        packet[6] = (byte)(xid >> 8);
        packet[7] = (byte)xid;
        packet[10] = type == MessageType.Inform ? (byte)0 : (byte)0x80;
        if (type == MessageType.Inform)
        {
            if (!DhcpTestService.UsableAddress(clientIp)) throw new ArgumentException("INFORM requires a configured IPv4 address.");
            IPAddress.Parse(clientIp!).GetAddressBytes().CopyTo(packet, 12); // ciaddr
        }
        mac.CopyTo(packet, 28);
        packet[236] = 99; packet[237] = 130; packet[238] = 83; packet[239] = 99;

        var opt = 240;
        packet[opt++] = 53; packet[opt++] = 1; packet[opt++] = (byte)type;

        // DORA identifies this test client by hardware type + MAC; INFORM does not claim the OS lease.
        if (type != MessageType.Inform)
        {
            packet[opt++] = 61; packet[opt++] = 7; packet[opt++] = 1;
            mac.CopyTo(packet, opt); opt += 6;
        }

        var host = Encoding.ASCII.GetBytes("PortStrider");
        packet[opt++] = 12; packet[opt++] = (byte)host.Length;
        host.CopyTo(packet, opt); opt += host.Length;

        if (type != MessageType.Inform && requestedIp is not null && IPAddress.TryParse(requestedIp, out var req) && req.AddressFamily == AddressFamily.InterNetwork)
        {
            packet[opt++] = 50; packet[opt++] = 4;
            req.GetAddressBytes().CopyTo(packet, opt); opt += 4;
        }

        if (type != MessageType.Inform && serverId is not null && IPAddress.TryParse(serverId, out var srv) && srv.AddressFamily == AddressFamily.InterNetwork)
        {
            packet[opt++] = 54; packet[opt++] = 4;
            srv.GetAddressBytes().CopyTo(packet, opt); opt += 4;
        }

        if (profile.DhcpOption == DhcpOptionKind.Option60 && !string.IsNullOrWhiteSpace(profile.DhcpVendorClass))
        {
            var vendor = Encoding.ASCII.GetBytes(profile.DhcpVendorClass);
            var len = Math.Min(vendor.Length, 255);
            packet[opt++] = 60; packet[opt++] = (byte)len;
            Array.Copy(vendor, 0, packet, opt, len); opt += len;
        }

        // Parameter Request List — this is what makes the server return 43/150 (and subnet/router/DNS/lease).
        var requested = new List<byte>(BaseParameterRequest);
        if (type == MessageType.Inform) requested.RemoveAll(code => code is 51 or 58 or 59);
        if (profile.DhcpOption is DhcpOptionKind.Option43 or DhcpOptionKind.Option60 && !requested.Contains(43)) requested.Add(43);
        if (profile.DhcpOption is DhcpOptionKind.Option150 && !requested.Contains(150)) requested.Add(150);
        if (profile.DhcpOption == DhcpOptionKind.Option60 && !requested.Contains(60)) requested.Add(60);
        packet[opt++] = 55; packet[opt++] = (byte)requested.Count;
        requested.CopyTo(packet, opt); opt += requested.Count;

        packet[opt++] = 255;
        var min = 300; // keep well above the 240-byte minimum so picky servers/relays accept it
        return packet[..Math.Max(opt, min)];
    }

    public static Reply? TryParseReply(byte[] buffer, IPEndPoint remote, int xid, byte[]? expectedMac = null)
    {
        if (buffer.Length < 240 || remote.Port != 67) return null;
        if (expectedMac is not null && (buffer[1] != 1 || buffer[2] != 6 || !buffer.AsSpan(28, 6).SequenceEqual(expectedMac))) return null;
        if (buffer[0] != 2) return null; // BOOTREPLY
        var replyXid = (buffer[4] << 24) | (buffer[5] << 16) | (buffer[6] << 8) | buffer[7];
        if (replyXid != xid) return null;
        if (buffer[236] != 99 || buffer[237] != 130 || buffer[238] != 83 || buffer[239] != 99) return null;

        var options = ParseOptions(buffer.AsSpan(240));
        if (!options.TryGetValue(53, out var typeText) || !byte.TryParse(typeText, out var typeByte)) return null;

        return new Reply
        {
            Type = (MessageType)typeByte,
            YourIp = new IPAddress(buffer.AsSpan(16, 4)).ToString(),
            Remote = remote,
            Options = options
        };
    }

    internal static Dictionary<int, string> ParseOptions(ReadOnlySpan<byte> options)
    {
        var map = new Dictionary<int, string>();
        var i = 0;
        while (i < options.Length)
        {
            var code = options[i++];
            if (code == 255) break;
            if (code == 0) continue;
            if (i >= options.Length) break;
            var len = options[i++];
            if (i + len > options.Length) break;
            var value = options.Slice(i, len);
            i += len;
            map[code] = Decode(code, value);
        }

        return map;
    }

    private static string Decode(int code, ReadOnlySpan<byte> value) => code switch
    {
        1 or 28 or 50 or 54 when value.Length >= 4 => Ip(value[..4]),
        3 or 6 or 42 or 150 => string.Join(", ", Chunk(value, 4).Select(Ip)),
        51 or 58 or 59 when value.Length >= 4 => ReadUInt32BigEndian(value).ToString(),
        53 when value.Length >= 1 => value[0].ToString(),
        12 or 15 or 60 => Encoding.ASCII.GetString(value).Trim('\0'),
        43 => DecodeVendorSpecific(value),
        _ => Convert.ToHexString(value)
    };

    private static string DecodeVendorSpecific(ReadOnlySpan<byte> value)
    {
        // Option 43 is opaque. If it is printable text (Aruba/Ruckus/Cisco often send controller IPs as ASCII) show it as
        // such; if it is exactly one or more IPv4 addresses show dotted quads; else hex.
        var printable = value.Length > 0 && value.ToArray().All(b => b is >= 0x20 and < 0x7F);
        if (printable) return Encoding.ASCII.GetString(value);
        if (value.Length > 0 && value.Length % 4 == 0 && value.Length <= 16) return string.Join(", ", Chunk(value, 4).Select(Ip));
        return Convert.ToHexString(value);
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> v) =>
        ((uint)v[0] << 24) | ((uint)v[1] << 16) | ((uint)v[2] << 8) | v[3];

    private static string Ip(byte[] bytes) => new IPAddress(bytes).ToString();
    private static string Ip(ReadOnlySpan<byte> bytes) => new IPAddress(bytes[..4].ToArray()).ToString();

    private static List<byte[]> Chunk(ReadOnlySpan<byte> data, int size)
    {
        var list = new List<byte[]>();
        for (var i = 0; i + size <= data.Length; i += size)
            list.Add(data.Slice(i, size).ToArray());
        return list;
    }

    public static byte[] ParseMac(string mac)
    {
        try
        {
            var parts = mac.Split([':', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 1 && parts[0].Length == 12)
                return Convert.FromHexString(parts[0]);
            return parts.Select(p => Convert.ToByte(p, 16)).ToArray();
        }
        catch
        {
            return [];
        }
    }
}
