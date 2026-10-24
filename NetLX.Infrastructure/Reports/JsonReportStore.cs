using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NetLX.Core.Models;
using NetLX.Core.Services;
using NetLX.Infrastructure.Platform;

namespace NetLX.Infrastructure.Reports;

public sealed class JsonReportStore : IReportStore
{
    private readonly string _dir;
    private readonly string _attachmentsDir;
    private readonly string _indexPath;
    private readonly List<SessionReport> _reports = [];
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public JsonReportStore()
    {
        _dir = Path.Combine(AppPaths.DataRoot, "sessions");
        _attachmentsDir = Path.Combine(_dir, "attachments");
        Directory.CreateDirectory(_attachmentsDir);
        _indexPath = Path.Combine(_dir, "index.json");
        MigrateLegacyIndex();
        Load();
    }

    public IReadOnlyList<SessionReport> List(string? search = null)
    {
        IEnumerable<SessionReport> query = _reports;
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(r =>
                r.AdapterName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.ProfileName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.JobLabel.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.SiteLabel.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.TechnicianNotes.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.AssetSerial.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        return query.ToArray();
    }

    public SessionReport? Get(Guid id) => _reports.FirstOrDefault(r => r.Id == id);

    public void Add(SessionReport report)
    {
        _reports.Insert(0, report);
        Persist();
        File.WriteAllText(Path.Combine(_dir, $"{report.Id}.json"), JsonSerializer.Serialize(report, _json));
    }

    public void Delete(Guid id)
    {
        _reports.RemoveAll(r => r.Id == id);
        var path = Path.Combine(_dir, $"{id}.json");
        if (File.Exists(path)) File.Delete(path);
        Persist();
    }

    public SessionReport Duplicate(Guid id)
    {
        var source = Get(id) ?? throw new InvalidOperationException("Report not found.");
        var copy = source with { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.Now, FinishedAt = DateTimeOffset.Now };
        Add(copy);
        return copy;
    }

    public Task AddAttachmentAsync(Guid reportId, string sourcePath, CancellationToken cancellationToken = default)
    {
        var report = Get(reportId) ?? throw new InvalidOperationException("Report not found.");
        var destName = $"{reportId}-{Path.GetFileName(sourcePath)}";
        var destPath = Path.Combine(_attachmentsDir, destName);
        File.Copy(sourcePath, destPath, overwrite: true);
        var attachment = new AttachmentInfo
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(sourcePath),
            ContentType = GuessContentType(sourcePath),
            StoredPath = destPath
        };
        var updated = report with { Attachments = report.Attachments.Append(attachment).ToArray() };
        Replace(updated);
        return Task.CompletedTask;
    }

    public Task ExportJsonAsync(SessionReport report, string path, CancellationToken cancellationToken = default)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(report, _json));
        return Task.CompletedTask;
    }

    public Task ExportCsvAsync(SessionReport report, string path, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Step,Status,Details");
        foreach (var step in report.Steps)
            sb.AppendLine($"{Escape(step.Title)},{Escape(step.Status.ToString())},{Escape(step.Details)}");
        foreach (var target in report.Targets)
            sb.AppendLine($"{Escape(target.Label)},{Escape(target.Status.ToString())},{Escape(target.Details)}");
        File.WriteAllText(path, sb.ToString());
        return Task.CompletedTask;
    }

    public Task ExportPdfAsync(SessionReport report, string path, CancellationToken cancellationToken = default)
    {
        WriteSimplePdf(path, PdfLines(report));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    internal static List<string> PdfLines(SessionReport report)
    {
        var lines = new List<string>
        {
            "netLX Session Report",
            $"Started: {report.StartedAt:u}",
        };
        if (report.FinishedAt > report.StartedAt)
            lines.Add($"Finished: {report.FinishedAt:u} ({(report.FinishedAt - report.StartedAt).TotalSeconds:0.#} s)");
        lines.Add($"Adapter: {report.AdapterName}");
        lines.Add($"Profile: {report.ProfileName}");
        foreach (var (label, value) in new[]
                 {
                     ("Job", report.JobLabel), ("Site", report.SiteLabel), ("Asset", report.AssetSerial),
                     ("Cable", report.CableLabel), ("Wall jack", report.WallJackLabel),
                     ("IPv4", report.Ipv4), ("IPv6", report.Ipv6), ("Gateway", report.Gateway)
                 })
        {
            if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{label}: {value}");
        }
        lines.Add($"Overall: {report.Overall}");
        lines.Add("");
        lines.Add("Test steps");
        lines.AddRange(report.Steps.Select(s => $"  {s.Title}: {s.Status} - {s.Details}"));
        if (report.Targets.Count > 0)
        {
            lines.Add("");
            lines.Add("Targets");
            lines.AddRange(report.Targets.Select(t => $"  {t.Label}: {t.Status} - {t.Details}{(t.ElapsedMs > 0 ? $" ({t.ElapsedMs} ms)" : "")}"));
        }
        if (!string.IsNullOrWhiteSpace(report.TechnicianNotes))
        {
            lines.Add("");
            lines.Add("Technician notes");
            lines.AddRange(report.TechnicianNotes.Replace("\r", "").Split('\n').Select(l => "  " + l));
        }
        return lines;
    }

    public Task ExportBundleAsync(SessionReport report, string zipPath, CancellationToken cancellationToken = default)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var jsonEntry = archive.CreateEntry("report.json");
        using (var writer = new StreamWriter(jsonEntry.Open()))
            writer.Write(JsonSerializer.Serialize(report, _json));

        foreach (var attachment in report.Attachments.Where(a => File.Exists(a.StoredPath)))
            archive.CreateEntryFromFile(attachment.StoredPath, Path.Combine("attachments", attachment.FileName));

        return Task.CompletedTask;
    }

    public Task<SessionReport> ImportBundleAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("report.json") ?? throw new InvalidOperationException("Bundle missing report.json.");
        using var reader = new StreamReader(entry.Open());
        var report = JsonSerializer.Deserialize<SessionReport>(reader.ReadToEnd(), _json)
            ?? throw new InvalidOperationException("Invalid report bundle.");
        var imported = report with { Id = Guid.NewGuid() };
        Add(imported);
        return Task.FromResult(imported);
    }

    private void Replace(SessionReport report)
    {
        var idx = _reports.FindIndex(r => r.Id == report.Id);
        if (idx >= 0) _reports[idx] = report;
        Persist();
        File.WriteAllText(Path.Combine(_dir, $"{report.Id}.json"), JsonSerializer.Serialize(report, _json));
    }

    private void Load()
    {
        if (!File.Exists(_indexPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<SessionReport>>(File.ReadAllText(_indexPath), _json);
            if (loaded is not null) _reports.AddRange(loaded.OrderByDescending(r => r.StartedAt));
        }
        catch
        {
            // corrupt index
        }
    }

    private void Persist() => File.WriteAllText(_indexPath, JsonSerializer.Serialize(_reports, _json));

    private void MigrateLegacyIndex()
    {
        var legacy = Path.Combine(AppPaths.DataRoot, "sessions.json");
        if (!File.Exists(legacy) || File.Exists(_indexPath)) return;
        try
        {
            Directory.CreateDirectory(_dir);
            File.Move(legacy, _indexPath);
        }
        catch
        {
            // ignore migration errors
        }
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    internal const int PdfLinesPerPage = 50;

    private static void WriteSimplePdf(string path, IEnumerable<string> sourceLines)
    {
        var pages = sourceLines
            .SelectMany(line => Wrap(line, 92))
            .Select(EscapePdfText)
            .Chunk(PdfLinesPerPage)
            .DefaultIfEmpty([])
            .ToArray();

        // 1 catalog, 2 page tree, 3 font, then a page + content stream pair per page.
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{string.Join(' ', pages.Select((_, i) => $"{4 + i * 2} 0 R"))}] /Count {pages.Length} >>"),
            Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
        };
        for (var i = 0; i < pages.Length; i++)
        {
            var content = new StringBuilder("BT\n/F1 10 Tf\n50 750 Td\n14 TL\n");
            foreach (var line in pages[i])
                content.Append('(').Append(line).Append(") Tj\nT*\n");
            content.Append("ET\nBT\n/F1 8 Tf\n50 30 Td\n(").Append($"netLX - page {i + 1} of {pages.Length}").Append(") Tj\nET\n");
            var contentBytes = Latin1(content.ToString());

            objects.Add(Encoding.ASCII.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>"));
            objects.Add([.. Encoding.ASCII.GetBytes($"<< /Length {contentBytes.Length} >>\nstream\n"), .. contentBytes, .. Encoding.ASCII.GetBytes("endstream")]);
        }

        using var stream = File.Create(path);
        WriteAscii(stream, "%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            WriteAscii(stream, $"{i + 1} 0 obj\n");
            stream.Write(objects[i]);
            WriteAscii(stream, "\nendobj\n");
        }

        var xref = stream.Position;
        WriteAscii(stream, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            WriteAscii(stream, $"{offset:0000000000} 00000 n \n");
        WriteAscii(stream, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    // Helvetica with WinAnsiEncoding covers Latin-1; anything outside it degrades to '?'.
    private static byte[] Latin1(string value) =>
        value.Select(c => c <= '\u00FF' ? (byte)c : (byte)'?').ToArray();

    private static IEnumerable<string> Wrap(string value, int width)
    {
        var normalized = value.Replace('—', '-').Replace('·', '-');
        if (normalized.Length == 0)
        {
            yield return "";
            yield break;
        }

        for (var offset = 0; offset < normalized.Length; offset += width)
            yield return normalized.Substring(offset, Math.Min(width, normalized.Length - offset));
    }

    private static string EscapePdfText(string value) =>
        value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".pcap" => "application/vnd.tcpdump.pcap",
        _ => "application/octet-stream"
    };
}
