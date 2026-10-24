using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NetLX.Core.Models;
using NetLX.Infrastructure.Probes;
using Xunit;

namespace NetLX.Infrastructure.Tests;

public class DnsDiagnosticsTests
{
    internal static byte[] Answer(byte[] query, DnsRecordType type = DnsRecordType.A)
    {
        var data = new List<byte>(query);
        data[2] = 0x81; data[3] = 0x80; data[7] = 1;
        var address = IPAddress.Parse(type == DnsRecordType.AAAA ? "2001:db8::42" : "192.0.2.42").GetAddressBytes();
        data.AddRange([0xc0, 12, 0, (byte)type, 0, 1, 0, 0, 0, 60, 0, (byte)address.Length]);
        data.AddRange(address);
        return data.ToArray();
    }

    [Theory]
    [InlineData(DnsRecordType.A, "192.0.2.42")]
    [InlineData(DnsRecordType.AAAA, "2001:db8::42")]
    public void Parses_address_and_ttl(DnsRecordType type, string address)
    {
        var query = DnsWire.BuildQuery(12, "example.com", type);
        Assert.True(DnsWire.TryParseResponse(Answer(query, type), 12, "example.com", type, out var reply));
        var answer = Assert.Single(reply!.Answers);
        Assert.Equal(address, answer.Value);
        Assert.Equal(60u, answer.Ttl);
    }

    [Theory]
    [InlineData(DnsRecordType.CNAME, "example.com")]
    [InlineData(DnsRecordType.NS, "example.com")]
    [InlineData(DnsRecordType.PTR, "example.com")]
    [InlineData(DnsRecordType.MX, "10 example.com")]
    [InlineData(DnsRecordType.TXT, "hello world")]
    public void Parses_compressed_names_mx_and_multistring_txt(DnsRecordType type, string expected)
    {
        var query = DnsWire.BuildQuery(12, "example.com", type);
        byte[] data = type switch
        {
            DnsRecordType.MX => [0, 10, 0xc0, 12],
            DnsRecordType.TXT => [5, .. System.Text.Encoding.ASCII.GetBytes("hello"), 5, .. System.Text.Encoding.ASCII.GetBytes("world")],
            _ => [0xc0, 12]
        };
        var reply = new List<byte>(query); reply[2] = 0x81; reply[3] = 0x80; reply[7] = 1;
        reply.AddRange([0xc0, 12, 0, (byte)type, 0, 1, 0, 0, 1, 44, 0, (byte)data.Length]);
        reply.AddRange(data);
        Assert.True(DnsWire.TryParseResponse(reply.ToArray(), 12, "example.com", type, out var parsed));
        var answer = Assert.Single(parsed!.Answers);
        Assert.Equal(expected, answer.Value);
        Assert.Equal(300u, answer.Ttl);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void Wildcard_source_is_not_reported_as_bound(string source)
        => Assert.Throws<ArgumentException>(() => BoundConnection.ParseSource(source));

    [Theory]
    [InlineData("a..example")]
    [InlineData(" ")]
    public void Rejects_invalid_names_instead_of_changing_query(string name) =>
        Assert.Throws<ArgumentException>(() => DnsWire.BuildAQuery(1, name));

    [Fact]
    public void Rejects_long_labels_and_normalizes_idn()
    {
        Assert.Throws<ArgumentException>(() => DnsWire.BuildAQuery(1, new string('a', 64) + ".com"));
        Assert.Equal("xn--bcher-kva.example", DnsWire.NormalizeName("Bücher.example."));
    }

    [Fact]
    public void Rejects_every_truncated_answer_and_wrong_questions()
    {
        var reply = Answer(DnsWire.BuildAQuery(7, "example.com"));
        for (var length = 0; length < reply.Length; length++)
            Assert.False(DnsWire.TryParseResponse(reply[..length], 7, "example.com", DnsRecordType.A, out _));
        Assert.False(DnsWire.TryParseResponse(reply, 8, "example.com", DnsRecordType.A, out _));
        Assert.False(DnsWire.TryParseResponse(reply, 7, "other.example", DnsRecordType.A, out _));
        Assert.False(DnsWire.TryParseResponse(reply, 7, "example.com", DnsRecordType.AAAA, out _));
        reply[2] = 1;
        Assert.False(DnsWire.TryParseResponse(reply, 7, "example.com", DnsRecordType.A, out _));
    }

    [Fact]
    public void Rejects_pointer_loops_and_unrelated_answers()
    {
        var query = DnsWire.BuildAQuery(7, "example.com");
        var loop = Answer(query); loop[query.Length + 1] = (byte)query.Length;
        Assert.False(DnsWire.TryParseResponse(loop, 7, "example.com", DnsRecordType.A, out _));
        var other = new List<byte>(query); other[2] = 0x81; other[7] = 1;
        other.AddRange([5, (byte)'o', (byte)'t', (byte)'h', (byte)'e', (byte)'r', 0, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 192, 0, 2, 1]);
        Assert.True(DnsWire.TryParseResponse(other.ToArray(), 7, "example.com", DnsRecordType.A, out var parsed));
        Assert.Empty(parsed!.Answers);
    }

    [Fact]
    public async Task Truncated_udp_retries_tcp_on_same_server_and_preserves_source()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            var packet = await udp.ReceiveAsync(deadline.Token);
            Assert.Equal("127.0.0.2", packet.RemoteEndPoint.Address.ToString());
            var truncated = (byte[])packet.Buffer.Clone(); truncated[2] = 0x83;
            await udp.SendAsync(truncated, packet.RemoteEndPoint, deadline.Token);
            using var client = await tcp.AcceptTcpClientAsync(deadline.Token);
            Assert.Equal("127.0.0.2", ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString());
            using var stream = client.GetStream();
            var size = new byte[2]; await stream.ReadExactlyAsync(size, deadline.Token);
            var query = new byte[BinaryPrimitives.ReadUInt16BigEndian(size)]; await stream.ReadExactlyAsync(query, deadline.Token);
            var reply = Answer(query); BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)reply.Length);
            await stream.WriteAsync(size, deadline.Token);
            await stream.WriteAsync(reply, deadline.Token);
        }, deadline.Token);
        var result = await new DnsLookupClient(port).QueryAsync("127.0.0.1", "example.com", DnsRecordType.A, 3000, "127.0.0.2", deadline.Token);
        await server;
        Assert.True(result.Success, result.Summary); Assert.Equal("TCP", result.Transport);
    }

    [Fact]
    public async Task Ignores_wrong_sender_and_wrong_question_then_accepts_matching_reply()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var rogue = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(async () =>
        {
            var packet = await server.ReceiveAsync(deadline.Token);
            var valid = Answer(packet.Buffer);
            var forged = (byte[])valid.Clone(); forged[^1] = 99;
            await rogue.SendAsync(forged, packet.RemoteEndPoint, deadline.Token);
            var wrongQuestion = (byte[])valid.Clone(); wrongQuestion[13] = (byte)'z';
            await server.SendAsync(wrongQuestion, packet.RemoteEndPoint, deadline.Token);
            await server.SendAsync(valid, packet.RemoteEndPoint, deadline.Token);
        }, deadline.Token);
        var result = await new DnsLookupClient(port).QueryAsync("127.0.0.1", "example.com", DnsRecordType.A, 2000, "127.0.0.1", deadline.Token);
        await serve;
        Assert.True(result.Success, result.Summary); Assert.Equal("192.0.2.42", Assert.Single(result.Answers).Value);
    }

    [Theory]
    [InlineData(0, "No A answer (NODATA)")]
    [InlineData(3, "NXDOMAIN")]
    public async Task Empty_or_negative_reply_does_not_pass(int rcode, string reason)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            var request = await udp.ReceiveAsync(deadline.Token);
            request.Buffer[2] = 0x81; request.Buffer[3] = (byte)rcode;
            await udp.SendAsync(request.Buffer, request.RemoteEndPoint, deadline.Token);
        });
        var result = await new DnsLookupClient(((IPEndPoint)udp.Client.LocalEndPoint!).Port).QueryAsync("127.0.0.1", "example.com", DnsRecordType.A, 1000, null, deadline.Token);
        await server;
        Assert.False(result.Success); Assert.Contains(reason, result.Summary);
    }
}
