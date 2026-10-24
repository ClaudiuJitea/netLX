using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NetLX.Core.Models;

namespace NetLX.Infrastructure.Probes;

internal sealed class DnsLookupClient(int port = 53)
{
    public async Task<DnsLookupResult> QueryAsync(string server, string name, DnsRecordType type, int timeoutMs,
        string? sourceAddress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var transport = "UDP";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IPAddress.TryParse(server, out var destination)) throw new ArgumentException("DNS server must be an IP address.");
            var source = BoundConnection.ParseSource(sourceAddress);
            if (source is not null && source.AddressFamily != destination.AddressFamily)
                throw new ArgumentException("DNS server and selected source use different address families.");
            name = DnsWire.NormalizeName(name);
            var id = (ushort)RandomNumberGenerator.GetInt32(65536);
            var query = DnsWire.BuildQuery(id, name, type);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);
            using var udp = new UdpClient(destination.AddressFamily);
            if (source is not null) udp.Client.Bind(new IPEndPoint(source, 0));
            udp.Connect(new IPEndPoint(destination, port)); // kernel rejects replies from other endpoints
            await udp.SendAsync(query, cts.Token);
            DnsResponse? reply;
            while (true)
            {
                var datagram = await udp.ReceiveAsync(cts.Token);
                if (!DnsWire.TryParseResponse(datagram.Buffer, id, name, type, out reply)) continue;
                break;
            }
            if (reply!.Truncated)
            {
                transport = "TCP";
                using var stream = await BoundConnection.ConnectAsync(server, port, sourceAddress, cts.Token);
                var request = new byte[query.Length + 2];
                BinaryPrimitives.WriteUInt16BigEndian(request, (ushort)query.Length);
                query.CopyTo(request, 2);
                await stream.WriteAsync(request, cts.Token);
                var size = new byte[2]; await stream.ReadExactlyAsync(size, cts.Token);
                var payload = new byte[BinaryPrimitives.ReadUInt16BigEndian(size)];
                await stream.ReadExactlyAsync(payload, cts.Token);
                if (!DnsWire.TryParseResponse(payload, id, name, type, out reply) || reply!.Truncated)
                    throw new InvalidDataException("Invalid DNS TCP response.");
            }
            return new DnsLookupResult
            {
                Server = server, Name = name, Type = type, ElapsedMs = sw.ElapsedMilliseconds, Transport = transport,
                Status = DnsWire.RcodeName(reply!.Rcode), Answers = reply.Answers,
                Error = reply.Rcode == 0 && !reply.Answers.Any(a => a.Type == type.ToString()) ? $"No {type} answer (NODATA)" : null
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new DnsLookupResult { Server = server, Name = name, Type = type, ElapsedMs = sw.ElapsedMilliseconds,
                Transport = transport, Error = ex is OperationCanceledException ? "timeout" : ex.Message };
        }
    }
}
