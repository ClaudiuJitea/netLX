using System.Net;
using System.Net.Sockets;
using PacketDotNet;
using NetLX.Core.Enums;
using NetLX.Core.Models;
using NetLX.Infrastructure.Probes;
using Xunit;

namespace NetLX.Infrastructure.Tests;

public class DhcpServiceTests
{
    private static AdapterInfo Adapter(string? ip = "192.0.2.20") => new()
    {
        Id = "test0", Name = "test0", MacAddress = "02:11:22:33:44:55", Ipv4 = ip
    };

    [Fact]
    public async Task Configured_adapter_checks_server_without_requesting_another_lease()
    {
        using var transport = new FakeTransport();
        var result = await new DhcpTestService(_ => transport).DiscoverAsync(Adapter(), TestProfile.Default(), false);
        Assert.Equal(TestStatus.Pass, result.Status);
        Assert.True(result.InformSent);
        Assert.True(result.AckReceived);
        Assert.False(result.RequestSent);
        Assert.False(result.LeaseAcquired);
        Assert.Null(result.OfferedIp);
        Assert.Null(result.LeaseTime);
        Assert.Equal("192.0.2.20", result.ExistingIp);
        Assert.Equal("192.0.2.1", result.ServerIp);
        Assert.Equal("192.0.2.53", result.Dns);
        var sent = Assert.Single(transport.Sent);
        Assert.Equal(IPAddress.Parse("192.0.2.20"), sent.Source);
        Assert.Equal(new byte[] { 192, 0, 2, 20 }, sent.Payload[12..16]);
        Assert.Equal(0, sent.Payload[10]);
        var options = DhcpPacket.ParseOptions(sent.Payload.AsSpan(240));
        Assert.Equal("8", options[53]);
        Assert.DoesNotContain(50, options.Keys);
        Assert.DoesNotContain(54, options.Keys);
        Assert.DoesNotContain(61, options.Keys);
        var requested = Convert.FromHexString(options[55]);
        Assert.DoesNotContain((byte)51, requested);
        Assert.DoesNotContain((byte)58, requested);
        Assert.DoesNotContain((byte)59, requested);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("169.254.1.20")]
    [InlineData("0.0.0.0")]
    public async Task Unconfigured_or_link_local_adapter_uses_full_dora(string? address)
    {
        using var transport = new FakeTransport();
        var result = await new DhcpTestService(_ => transport).DiscoverAsync(Adapter(address), TestProfile.Default(), false);
        Assert.Equal(TestStatus.Pass, result.Status);
        Assert.True(result.LeaseAcquired);
        Assert.False(result.InformSent);
        Assert.True(result.RequestSent);
        Assert.Equal("192.0.2.30", result.OfferedIp);
        Assert.Equal(2, transport.Sent.Count);
        Assert.All(transport.Sent, s => Assert.Equal(IPAddress.Any, s.Source));
    }

    [Fact]
    public async Task Existing_ip_without_ack_is_not_a_pass_or_a_false_lease_failure()
    {
        using var transport = new FakeTransport { Timeout = true };
        var result = await new DhcpTestService(_ => transport).DiscoverAsync(Adapter(), TestProfile.Default(), false);
        Assert.Equal(TestStatus.Warning, result.Status);
        Assert.False(result.AckReceived);
        Assert.False(result.LeaseAcquired);
        Assert.Contains("not verified", result.Summary);
    }

    [Fact]
    public async Task No_offer_still_fails_on_an_unconfigured_adapter()
    {
        using var transport = new FakeTransport { Timeout = true };
        var result = await new DhcpTestService(_ => transport).DiscoverAsync(Adapter(null), TestProfile.Default(), false);
        Assert.Equal(TestStatus.Fail, result.Status);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_ack_propagates_and_disposes_transport()
    {
        using var cts = new CancellationTokenSource();
        var transport = new FakeTransport { OnReceive = () => cts.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DhcpTestService(_ => transport).DiscoverAsync(Adapter(), TestProfile.Default(), false, cts.Token));
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task Wrong_transaction_mac_and_server_are_ignored()
    {
        using var transport = new FakeTransport { InjectUnrelated = true };
        var result = await new DhcpTestService(_ => transport).DiscoverAsync(Adapter(null), TestProfile.Default(), false);
        Assert.True(result.LeaseAcquired);
        Assert.Equal("192.0.2.1", result.ServerIp);
        Assert.True(transport.ReadCount >= 8);
    }

    [Fact]
    public async Task Nak_from_selected_server_is_failure()
    {
        using var transport = new FakeTransport { Nak = true };
        var result = await new DhcpTestService(_ => transport).DiscoverAsync(Adapter(null), TestProfile.Default(), false);
        Assert.Equal(TestStatus.Fail, result.Status);
        Assert.False(result.LeaseAcquired);
    }

    [Fact]
    public async Task Transport_failure_reports_unavailable_instead_of_network_failure()
    {
        var result = await new DhcpTestService(_ => throw new SocketException()).DiscoverAsync(Adapter(), TestProfile.Default(), false);
        Assert.False(result.Supported);
        Assert.Equal(TestStatus.Skipped, result.Status);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.20")]
    public void Ethernet_transport_uses_correct_source_ports_and_checksums(string source)
    {
        var payload = new byte[301]; // odd length also exercises UDP checksum padding
        var bytes = PcapDhcpTransport.BuildFrame(payload, [2, 17, 34, 51, 68, 85], IPAddress.Parse(source));
        var frame = Packet.ParsePacket(LinkLayers.Ethernet, bytes);
        var ip = frame.Extract<IPv4Packet>();
        var udp = frame.Extract<UdpPacket>();
        Assert.Equal(IPAddress.Parse(source), ip.SourceAddress);
        Assert.Equal(IPAddress.Broadcast, ip.DestinationAddress);
        Assert.Equal(68, udp.SourcePort);
        Assert.Equal(67, udp.DestinationPort);
        Assert.True(ip.ValidIPChecksum);
        Assert.True(udp.ValidUdpChecksum);
        Assert.Equal(payload, udp.PayloadData);
    }

    private sealed class FakeTransport : IDhcpTransport
    {
        public readonly List<(byte[] Payload, IPAddress Source)> Sent = [];
        private readonly Queue<UdpReceiveResult> _replies = new();
        public bool Timeout { get; init; }
        public bool Nak { get; init; }
        public bool InjectUnrelated { get; init; }
        public Action? OnReceive { get; init; }
        public bool Disposed { get; private set; }
        public int ReadCount { get; private set; }

        public Task SendAsync(byte[] payload, IPAddress source, CancellationToken cancellationToken)
        {
            Sent.Add((payload, source));
            var type = DhcpPacket.ParseOptions(payload.AsSpan(240))[53];
            var reply = new byte[300];
            reply[0] = 2; reply[1] = 1; reply[2] = 6;
            payload.AsSpan(4, 4).CopyTo(reply.AsSpan(4));
            payload.AsSpan(28, 6).CopyTo(reply.AsSpan(28));
            if (type != "8") new byte[] { 192, 0, 2, 30 }.CopyTo(reply, 16);
            new byte[] { 99, 130, 83, 99 }.CopyTo(reply, 236);
            new byte[] { 53, 1, (byte)(type == "1" ? 2 : Nak ? 6 : 5),
                54, 4, 192, 0, 2, 1, 6, 4, 192, 0, 2, 53, 255 }.CopyTo(reply, 240);
            if (InjectUnrelated)
            {
                var badXid = (byte[])reply.Clone(); badXid[7] ^= 0xFF; Enqueue(badXid);
                var badMac = (byte[])reply.Clone(); badMac[28] ^= 0xFF; Enqueue(badMac);
                var badServer = (byte[])reply.Clone(); badServer[245] = 0; Enqueue(badServer);
                if (type == "3")
                {
                    var otherServer = (byte[])reply.Clone(); otherServer[248] = 2; Enqueue(otherServer);
                    var otherAddress = (byte[])reply.Clone(); otherAddress[19] = 99; Enqueue(otherAddress);
                }
            }
            Enqueue(reply);
            return Task.CompletedTask;
        }

        private void Enqueue(byte[] bytes) => _replies.Enqueue(new UdpReceiveResult(bytes, new IPEndPoint(IPAddress.Parse("192.0.2.1"), 67)));

        public Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            OnReceive?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Timeout) throw new OperationCanceledException();
            return Task.FromResult(_replies.Dequeue());
        }
        public void Dispose() => Disposed = true;
    }
}
