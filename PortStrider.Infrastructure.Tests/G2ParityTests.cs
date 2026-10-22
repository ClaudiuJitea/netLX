using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PortStrider.Core.Models;
using PortStrider.Infrastructure.Probes;
using PortStrider.Infrastructure.Profiles;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public class G2ParityTests
{
    [Fact]
    public async Task Http_probe_authenticates_to_a_proxy_without_sending_credentials_to_the_target()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var proxy = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var socket = await listener.AcceptTcpClientAsync(cts.Token);
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var headers = new List<string>();
                while (await reader.ReadLineAsync(cts.Token) is { Length: > 0 } line) headers.Add(line);
                Assert.StartsWith("GET http://example.invalid/", headers[0]);
                Assert.DoesNotContain(headers, h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
                if (attempt == 0)
                {
                    Assert.DoesNotContain(headers, h => h.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cts.Token);
                }
                else
                {
                    Assert.Contains("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("tech:secret")), headers);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cts.Token);
                }
            }
        }, cts.Token);
        var result = await new ProbeService().ProbeHttpAsync("http://example.invalid/", $"http://127.0.0.1:{port}", cts.Token, "tech", "secret");
        await proxy;
        Assert.Null(result.Error);
        Assert.Equal(200, result.StatusCode);
    }

    [Theory]
    [InlineData("bad proxy")]
    [InlineData("ftp://proxy.example")]
    [InlineData("http://user:password@proxy.example")]
    public async Task Invalid_proxy_is_reported_without_echoing_credentials(string url)
    {
        var result = await new ProbeService().ProbeHttpAsync("http://example.invalid", url);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain(url, result.Error);
    }

    [Fact]
    public async Task Passwords_survive_local_serialization_but_are_omitted_from_export()
    {
        var profile = TestProfile.Default() with { ProxyUrl = "http://proxy.example:8080", ProxyUsername = "tech", ProxyPassword = "secret", Password8021X = "eap-secret" };
        var restored = JsonSerializer.Deserialize<TestProfile>(JsonSerializer.Serialize(profile));
        Assert.Equal("secret", restored!.ProxyPassword);
        Assert.Equal("eap-secret", restored.Password8021X);
        var path = Path.GetTempFileName();
        try
        {
            await new JsonProfileStore().ExportAsync(profile, path);
            var exported = JsonSerializer.Deserialize<TestProfile>(await File.ReadAllTextAsync(path));
            Assert.Null(exported!.ProxyPassword);
            Assert.Null(exported.Password8021X);
            Assert.Equal("tech", exported.ProxyUsername);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null, "fe80::1%2", "Link-local fe80::1%2")]
    [InlineData("2001:db8::1", "fe80::1%2", "Link-local fe80::1%2 · Global 2001:db8::1")]
    [InlineData("fd00::1", null, "Unique-local fd00::1")]
    [InlineData(null, null, "No IPv6 address on adapter")]
    public void Ipv6_reports_link_local_global_and_unique_local_addresses(string? global, string? local, string expected)
    {
        var adapter = new AdapterInfo { Id = "test", Name = "test", Ipv6 = global, Ipv6LinkLocal = local };
        Assert.Equal(expected, Ipv6Addressing.Describe(adapter));
    }
}
