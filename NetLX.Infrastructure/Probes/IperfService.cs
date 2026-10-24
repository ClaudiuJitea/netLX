using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using NetLX.Core.Models;
using NetLX.Core.Services;
using NetLX.Infrastructure.Platform;

namespace NetLX.Infrastructure.Probes;

public sealed class IperfService : IIperfService
{
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;

    public bool IsServerRunning => _serverTask is { IsCompleted: false };

    public async Task<IperfResult> RunClientAsync(IperfOptions options, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        if (!await ProcessUtil.CommandExistsAsync("iperf3", cancellationToken))
        {
            return new IperfResult { Success = false, Error = "iperf3 is not installed or not on PATH." };
        }

        var args = BuildClientArguments(options);
        progress.Report("iperf3 " + string.Join(' ', args));
        var result = await ProcessUtil.RunAsync("iperf3", args, cancellationToken);
        if (result is null)
            return new IperfResult { Success = false, Error = "Failed to start iperf3" };
        return ParseClientOutput(result.StandardOutput, result.StandardError, result.ExitCode, options);
    }

    internal static IReadOnlyList<string> BuildClientArguments(IperfOptions options)
    {
        var args = new List<string>
        {
            "-c", options.Host,
            "-p", options.Port.ToString(CultureInfo.InvariantCulture),
            "-t", Math.Clamp(options.Seconds, 1, 3600).ToString(CultureInfo.InvariantCulture),
            "-J"
        };
        var streams = Math.Clamp(options.Streams, 1, 128);
        if (streams > 1) args.AddRange(["-P", streams.ToString(CultureInfo.InvariantCulture)]);
        if (options.Protocol == IperfProtocol.Udp)
            args.AddRange(["-u", "-b", $"{Math.Clamp(options.UdpBandwidthMbps, 1, 100_000)}M"]);
        switch (options.Direction)
        {
            case IperfDirection.Download: args.Add("-R"); break;
            case IperfDirection.Bidirectional: args.Add("--bidir"); break;
        }
        return args;
    }

    internal static IperfResult ParseClientOutput(string stdout, string stderr, int exitCode, IperfOptions options)
    {
        var output = stdout + stderr;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return new IperfResult { Success = false, Error = $"iperf3: {err.GetString()}", RawOutput = output };

            var end = root.GetProperty("end");
            var sent = Bps(end, "sum_sent");
            var received = Bps(end, "sum_received");
            double? jitter = null, lost = null;
            long? retransmits = null;
            if (end.TryGetProperty("sum_sent", out var sumSent) && sumSent.TryGetProperty("retransmits", out var rt))
                retransmits = rt.GetInt64();
            // UDP results live in "sum" (older iperf3) and/or "sum_received" (3.13+).
            foreach (var name in new[] { "sum", "sum_received" })
            {
                if (!end.TryGetProperty(name, out var sum)) continue;
                if (sum.TryGetProperty("jitter_ms", out var j)) jitter = j.GetDouble();
                if (sum.TryGetProperty("lost_percent", out var l)) lost = l.GetDouble();
            }
            if (options.Protocol == IperfProtocol.Udp) received ??= Bps(end, "sum");

            var bps = received ?? sent ?? 0;
            var parts = new List<string>();
            var direction = options.Direction switch
            {
                IperfDirection.Download => "download",
                IperfDirection.Bidirectional => "bidirectional",
                _ => "upload"
            };
            parts.Add($"{options.Protocol.ToString().ToUpperInvariant()} {direction}");
            if (sent is not null) parts.Add($"sent {AdapterInfo.FormatSpeed((long)sent)}");
            if (received is not null) parts.Add($"received {AdapterInfo.FormatSpeed((long)received)}");
            if (retransmits is not null && options.Protocol == IperfProtocol.Tcp) parts.Add($"{retransmits} retransmits");
            if (jitter is not null) parts.Add($"jitter {jitter:0.###} ms");
            if (lost is not null) parts.Add($"loss {lost:0.##}%");

            return new IperfResult
            {
                Success = exitCode == 0,
                BitsPerSecond = bps,
                SentBitsPerSecond = sent,
                ReceivedBitsPerSecond = received,
                Retransmits = retransmits,
                JitterMs = jitter,
                LostPercent = lost,
                Summary = string.Join(" · ", parts),
                Error = exitCode == 0 ? null : stderr.Trim() is { Length: > 0 } e ? e : $"iperf3 exited with code {exitCode}",
                RawOutput = output
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            var match = Regex.Match(output, @"([\d.]+)\s+([GMK]?)bits/sec");
            return new IperfResult
            {
                Success = exitCode == 0,
                Summary = match.Success ? match.Value : (exitCode == 0 ? "Completed" : "iperf3 failed"),
                Error = exitCode == 0 ? null : stderr,
                RawOutput = output
            };
        }
    }

    private static double? Bps(JsonElement end, string name) =>
        end.TryGetProperty(name, out var sum) && sum.TryGetProperty("bits_per_second", out var bps) ? bps.GetDouble() : null;

    public async Task StartServerAsync(int port, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        if (IsServerRunning) return;
        if (!await ProcessUtil.CommandExistsAsync("iperf3", cancellationToken))
            throw new InvalidOperationException("iperf3 is not installed or not on PATH.");
        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _serverCts.Token;
        _serverTask = Task.Run(async () =>
        {
            progress.Report($"iperf3 -s -p {port}");
            var exe = ProcessUtil.FindCommand("iperf3") ?? "iperf3";
            try
            {
                await Cli.Wrap(exe)
                    .WithArguments(["-s", "-p", port.ToString()])
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(ct);
            }
            catch (OperationCanceledException)
            {
                progress.Report("iPerf server stopped");
            }
            catch (Exception ex)
            {
                progress.Report(ex.Message);
            }
        }, ct);
    }

    public async Task StopServerAsync()
    {
        if (_serverCts is null) return;
        await _serverCts.CancelAsync();
        try { if (_serverTask is not null) await _serverTask; }
        catch { /* cancelled */ }
        _serverCts.Dispose();
        _serverCts = null;
        _serverTask = null;
    }
}
