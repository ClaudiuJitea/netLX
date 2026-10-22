using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PortStrider.Infrastructure.Probes;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public class ProbeReliabilityTests
{
    [Fact]
    public async Task Http_binds_the_requested_source_and_exposes_final_url()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            Assert.Equal("127.0.0.2", ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString());
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
        });
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/health";
        var result = await new ProbeService().ProbeHttpAsync(url, cancellationToken: deadline.Token, sourceAddress: "127.0.0.2");
        await server;
        Assert.True(result.Success, result.Error); Assert.Equal(url, result.FinalUrl); Assert.Equal("127.0.0.2", result.SourceAddress);
    }

    [Fact]
    public async Task Url_containing_1_1_1_1_never_bypasses_certificate_validation()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var ssl = new SslStream(client.GetStream());
            try { await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, deadline.Token); }
            catch (System.Security.Authentication.AuthenticationException) { }
            catch (IOException) { }
        });
        var result = await new ProbeService().ProbeHttpAsync($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/1.1.1.1", cancellationToken: deadline.Token);
        await server;
        Assert.False(result.Success); Assert.NotNull(result.CertificateError); Assert.Contains("TLS validation failed", result.Error);
    }

    [Fact]
    public async Task Tcp_supports_ipv6_source_without_an_ipv4_socket()
    {
        if (!Socket.OSSupportsIPv6) return;
        using var server = new TcpListener(IPAddress.IPv6Loopback, 0); server.Start();
        var result = await new ProbeService().ProbePortAsync("::1", ((IPEndPoint)server.LocalEndpoint).Port, 1000, "::1");
        Assert.True(result.Open, result.Error);
    }

    [Fact]
    public async Task Incompatible_source_is_not_silently_ignored()
    {
        var result = await new ProbeService().ProbePortAsync("::1", 80, 100, "127.0.0.1");
        Assert.False(result.Open); Assert.Contains("address family", result.Error);
        var dns = await new ProbeService().LookupDnsAsync("::1", "example.com", Core.Models.DnsRecordType.A, sourceAddress: "127.0.0.1");
        Assert.False(dns.Success); Assert.Contains("different address families", dns.Error);
    }

    [Fact]
    public async Task Probes_propagate_cancellation()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var probes = new ProbeService();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probes.PingOnceAsync("127.0.0.1", 1000, cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probes.ProbePortAsync("127.0.0.1", 80, 1000, cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probes.ProbeHttpAsync("http://127.0.0.1", cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probes.LookupDnsAsync("127.0.0.1", "example.com", Core.Models.DnsRecordType.A, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Linux_ping_uses_selected_source()
    {
        if (!OperatingSystem.IsLinux() || PlatformPingMissing()) return;
        var result = await new ProbeService().PingOnceAsync("127.0.0.1", 1000, "127.0.0.2");
        Assert.True(result.Success, result.Status); Assert.True(result.SourceBound);
    }
    private static bool PlatformPingMissing() => PortStrider.Infrastructure.Platform.ProcessUtil.FindCommand("ping") is null;
}
