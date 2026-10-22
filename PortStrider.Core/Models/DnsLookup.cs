namespace PortStrider.Core.Models;

public enum DnsRecordType : ushort { A = 1, NS = 2, CNAME = 5, PTR = 12, MX = 15, TXT = 16, AAAA = 28 }

public sealed record DnsAnswer(string Name, string Type, uint Ttl, string Value);

public sealed class DnsLookupResult
{
    public string Server { get; init; } = "";
    public string Name { get; init; } = "";
    public DnsRecordType Type { get; init; }
    public long ElapsedMs { get; init; }
    public string Transport { get; init; } = "UDP";
    public string Status { get; init; } = "";
    public string? Error { get; init; }
    public IReadOnlyList<DnsAnswer> Answers { get; init; } = [];
    public bool Success => Error is null && Status == "NOERROR" && Answers.Any(a => a.Type == Type.ToString());
    public string Summary => $"{Server} · {Transport} · {ElapsedMs} ms · {Error ?? Status}";
}
