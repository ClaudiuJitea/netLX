using System.Net;
using NetLX.Core.Models;

namespace NetLX.Infrastructure.Probes;

internal static class Ipv6Addressing
{
    public static string Describe(AdapterInfo adapter)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(adapter.Ipv6LinkLocal)) parts.Add($"Link-local {adapter.Ipv6LinkLocal}");
        if (!string.IsNullOrWhiteSpace(adapter.Ipv6))
        {
            var uniqueLocal = IPAddress.TryParse(adapter.Ipv6, out var ip) && (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
            parts.Add($"{(uniqueLocal ? "Unique-local" : "Global")} {adapter.Ipv6}");
        }
        return parts.Count == 0 ? "No IPv6 address on adapter" : string.Join(" · ", parts);
    }
}
