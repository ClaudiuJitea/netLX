using NetLX.Core.Models;
using NetLX.Infrastructure.Reports;
using Xunit;

namespace NetLX.Infrastructure.Tests;

public sealed class ReportStoreTests
{
    [Fact]
    public async Task ExportPdf_WritesValidPdfEnvelope()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netlx-{Guid.NewGuid():N}.pdf");
        try
        {
            var report = new SessionReport
            {
                Id = Guid.NewGuid(),
                StartedAt = DateTimeOffset.UtcNow,
                AdapterName = "eth0",
                ProfileName = "Default",
                Overall = "PASS",
                Steps =
                [
                    new TestStepUpdate
                    {
                        Id = "link",
                        Title = "Physical link",
                        Status = Core.Enums.TestStatus.Pass,
                        Details = "1 Gbps"
                    }
                ]
            };

            await new JsonReportStore().ExportPdfAsync(report, path);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(bytes));
            Assert.EndsWith("%%EOF\n", System.Text.Encoding.ASCII.GetString(bytes));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportPdf_SplitsLongReportsAcrossPagesAndIncludesTargets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netlx-{Guid.NewGuid():N}.pdf");
        try
        {
            var report = new SessionReport
            {
                Id = Guid.NewGuid(),
                StartedAt = DateTimeOffset.UtcNow,
                AdapterName = "eth0",
                Overall = "PASS",
                Steps = Enumerable.Range(1, 120).Select(i => new TestStepUpdate
                {
                    Id = $"s{i}",
                    Title = $"Step {i}",
                    Status = Core.Enums.TestStatus.Pass,
                    Details = "ok"
                }).ToArray(),
                Targets =
                [
                    new TargetTestResult { TargetId = "t", Label = "Gateway", Status = Core.Enums.TestStatus.Pass, Details = "2 ms" }
                ]
            };

            await new JsonReportStore().ExportPdfAsync(report, path);

            var text = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path));
            var lines = JsonReportStore.PdfLines(report).Count;
            var expectedPages = (lines + JsonReportStore.PdfLinesPerPage - 1) / JsonReportStore.PdfLinesPerPage;
            Assert.Contains($"/Count {expectedPages}", text);
            Assert.True(expectedPages >= 3);
            Assert.Contains("(  Step 120: Pass - ok) Tj", text);
            Assert.Contains("(  Gateway: Pass - 2 ms) Tj", text);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
