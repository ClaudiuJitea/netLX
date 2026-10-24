using NetLX.Core.Enums;
using NetLX.Core.Models;
using Xunit;

namespace NetLX.Core.Tests;

public class TargetValidationTests
{
    [Theory]
    [InlineData(TargetKind.Ping, "example.com", 0, "", true)]
    [InlineData(TargetKind.Ping, "2001:db8::1", 0, "", true)]
    [InlineData(TargetKind.Ping, "-bad host", 0, "", false)]
    [InlineData(TargetKind.TcpPort, "example.com", 65536, "", false)]
    [InlineData(TargetKind.TcpPort, "example.com", 443, "", true)]
    [InlineData(TargetKind.Http, "", 0, "https://example.com/health", true)]
    [InlineData(TargetKind.Http, "", 0, "file:///etc/passwd", false)]
    [InlineData(TargetKind.Http, "", 0, "https://user:secret@example.com", false)]
    public void Validates_targets_before_profile_save(TargetKind kind, string host, int port, string url, bool valid)
    {
        var target = new TestTarget { Id = "test", Label = "Service", Kind = kind, Host = host, Port = port, Url = url };
        Assert.Equal(valid, TargetValidation.Error(target) is null);
    }

    [Fact]
    public void Source_selection_handles_ipv6_literals_and_http_urls()
    {
        var adapter = new AdapterInfo { Id = "test", Name = "test", Ipv4 = "192.0.2.1", Ipv6 = "2001:db8::1" };
        Assert.Equal(adapter.Ipv6, adapter.SourceFor("2001:db8::2"));
        Assert.Equal(adapter.Ipv6, adapter.SourceFor("https://[2001:db8::2]/"));
        Assert.Equal(adapter.Ipv4, adapter.SourceFor("https://example.com/"));
    }
}
