using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using NetLX.Core.Models;
using NetLX.Infrastructure.Platform;

namespace NetLX.Infrastructure.Probes;

internal static class SourcePing
{
    public static async Task<PingSample> RunAsync(string host, int timeoutMs, string source, CancellationToken ct)
    {
        var executable = ProcessUtil.FindCommand("ping") ?? throw new InvalidOperationException("Install iputils-ping for source-bound ICMP tests.");
        var sourceIp = BoundConnection.ParseSource(source)!;
        var addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct);
        var target = addresses.FirstOrDefault(a => a.AddressFamily == sourceIp.AddressFamily)
            ?? throw new InvalidOperationException("No destination address matches the selected source address family.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeoutMs + 1000);
        var psi = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["LC_ALL"] = "C";
        foreach (var arg in new[] { "-n", "-c", "1", "-W", (Math.Max(1, timeoutMs) / 1000d).ToString("0.###", CultureInfo.InvariantCulture), "-I", source, target.ToString() }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start ping.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var text = await stdout;
            var error = (await stderr).Trim();
            var match = Regex.Match(text, @"time[=<]([\d.]+)\s*ms");
            var time = match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var ms) ? (long)Math.Round(ms) : 0;
            return new PingSample { Sequence = 0, Success = process.ExitCode == 0, RoundtripMs = time, SourceBound = true,
                Status = process.ExitCode == 0 ? $"Success · source {source}" : string.IsNullOrEmpty(error) ? $"No reply · source {source}" : error };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PingSample { Sequence = 0, Success = false, SourceBound = true, Status = "timeout" };
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
