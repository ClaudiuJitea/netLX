using NetLX.Core.Models;
using NetLX.Infrastructure.Probes;
using Xunit;

namespace NetLX.Infrastructure.Tests;

public sealed class IperfServiceTests
{
    [Fact]
    public void BuildClientArguments_MapsUdpDownloadAndStreams()
    {
        var args = IperfService.BuildClientArguments(new IperfOptions
        {
            Host = "10.0.0.5",
            Port = 5202,
            Seconds = 5,
            Protocol = IperfProtocol.Udp,
            Direction = IperfDirection.Download,
            Streams = 4,
            UdpBandwidthMbps = 250
        });

        Assert.Equal(["-c", "10.0.0.5", "-p", "5202", "-t", "5", "-J", "-P", "4", "-u", "-b", "250M", "-R"], args);
    }

    [Fact]
    public void BuildClientArguments_DefaultTcpUploadHasNoExtraFlags()
    {
        var args = IperfService.BuildClientArguments(new IperfOptions { Host = "h" });
        Assert.Equal(["-c", "h", "-p", "5201", "-t", "10", "-J"], args);
    }

    [Fact]
    public void ParseClientOutput_TcpReportsSentReceivedAndRetransmits()
    {
        const string json = """
            {"end":{"sum_sent":{"bits_per_second":940000000,"retransmits":12},
                    "sum_received":{"bits_per_second":935000000}}}
            """;
        var result = IperfService.ParseClientOutput(json, "", 0, new IperfOptions { Host = "h" });

        Assert.True(result.Success);
        Assert.Equal(935000000, result.BitsPerSecond);
        Assert.Equal(940000000, result.SentBitsPerSecond);
        Assert.Equal(12, result.Retransmits);
        Assert.Contains("12 retransmits", result.Summary);
        Assert.StartsWith("TCP upload", result.Summary);
    }

    [Fact]
    public void ParseClientOutput_UdpReportsJitterAndLoss()
    {
        const string json = """
            {"end":{"sum":{"bits_per_second":99000000,"jitter_ms":0.042,"lost_packets":3,"lost_percent":0.5}}}
            """;
        var result = IperfService.ParseClientOutput(json, "", 0, new IperfOptions { Host = "h", Protocol = IperfProtocol.Udp });

        Assert.Equal(99000000, result.ReceivedBitsPerSecond);
        Assert.Equal(0.042, result.JitterMs);
        Assert.Equal(0.5, result.LostPercent);
        Assert.Contains("loss 0.5%", result.Summary);
    }

    [Fact]
    public void ParseClientOutput_SurfacesIperfErrorField()
    {
        const string json = """{"start":{},"end":{},"error":"unable to connect to server: Connection refused"}""";
        var result = IperfService.ParseClientOutput(json, "", 1, new IperfOptions { Host = "h" });

        Assert.False(result.Success);
        Assert.Equal("iperf3: unable to connect to server: Connection refused", result.Error);
    }
}
