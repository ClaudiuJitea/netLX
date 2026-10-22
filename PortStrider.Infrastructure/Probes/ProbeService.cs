using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Probes;

public sealed class ProbeService : IProbeService
{
    private static readonly Dictionary<int, string> WellKnown = new()
    {
        [22] = "SSH",
        [23] = "Telnet",
        [53] = "DNS",
        [80] = "HTTP",
        [123] = "NTP",
        [161] = "SNMP",
        [443] = "HTTPS",
        [445] = "SMB",
        [3389] = "RDP",
        [5060] = "SIP",
        [8080] = "HTTP-alt",
        [8443] = "HTTPS-alt"
    };

    public async Task<PingSample> PingOnceAsync(string host, int timeoutMs, string? sourceAddress = null, CancellationToken cancellationToken = default)
    {
        using var ping = new Ping();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(sourceAddress) && OperatingSystem.IsLinux())
                return await SourcePing.RunAsync(host, timeoutMs, sourceAddress, cancellationToken);
            var reply = await ping.SendPingAsync(host, timeoutMs).WaitAsync(cancellationToken);
            return new PingSample
            {
                Sequence = 0,
                Success = reply.Status == IPStatus.Success,
                RoundtripMs = reply.RoundtripTime,
                Status = string.IsNullOrWhiteSpace(sourceAddress)
                    ? reply.Status.ToString()
                    : $"{reply.Status} (route via OS; source bind not supported for ICMP)"
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new PingSample { Sequence = 0, Success = false, Status = ex.Message };
        }
    }

    public async Task<IReadOnlyList<TraceHop>> TracerouteAsync(string host, IProgress<TraceHop> progress, CancellationToken cancellationToken = default)
    {
        host = (IPAddress.TryParse(host, out var literal) ? literal : (await Dns.GetHostAddressesAsync(host, cancellationToken)).First()).ToString();
        if (HostOs.IsWindows)
            return await RunProcessTraceAsync("tracert", ["-d", "-w", "1000", "-h", "20", host], ParseWindows, progress, cancellationToken);

        if (await ProcessUtil.CommandExistsAsync("traceroute", cancellationToken))
            return await RunProcessTraceAsync("traceroute", ["-n", "-w", "1", "-q", "1", "-m", "20", host], ParseUnix, progress, cancellationToken);

        if (await ProcessUtil.CommandExistsAsync("tracepath", cancellationToken))
            return await RunProcessTraceAsync("tracepath", ["-n", host], ParseUnix, progress, cancellationToken);

        return await FallbackTtlPingAsync(host, progress, cancellationToken);
    }

    public async Task<PortProbeResult> ProbePortAsync(string host, int port, int timeoutMs, string? sourceAddress = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);
            using var stream = await BoundConnection.ConnectAsync(host, port, sourceAddress, cts.Token);
            sw.Stop();
            return new PortProbeResult
            {
                Port = port,
                Service = WellKnown.GetValueOrDefault(port, "TCP"),
                Open = true,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            return new PortProbeResult
            {
                Port = port,
                Service = WellKnown.GetValueOrDefault(port, "TCP"),
                Open = false,
                ElapsedMs = sw.ElapsedMilliseconds,
                Error = ex is OperationCanceledException or TimeoutException ? "timeout" : ex.Message
            };
        }
    }

    public async Task<HttpProbeResult> ProbeHttpAsync(string url, string? proxyUrl = null, CancellationToken cancellationToken = default, string? proxyUsername = null, string? proxyPassword = null, string? sourceAddress = null)
    {
        string? subject = null, issuer = null, tls = null, certificateError = null;
        DateTimeOffset? notAfter = null;

        using var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    if (cert is X509Certificate2 c2)
                    {
                        subject = c2.Subject;
                        issuer = c2.Issuer;
                        notAfter = c2.NotAfter;
                    }
                    else if (cert is not null)
                    {
                        subject = cert.Subject;
                        issuer = cert.Issuer;
                    }

                    certificateError = errors == SslPolicyErrors.None ? null : errors.ToString();
                    return errors == SslPolicyErrors.None;
                }
            }
        };

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
                throw new ArgumentException("Enter an HTTP(S) URL.");
            if (!string.IsNullOrWhiteSpace(sourceAddress))
            {
                _ = BoundConnection.ParseSource(sourceAddress);
                handler.ConnectCallback = async (context, ct) =>
                    await BoundConnection.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, sourceAddress, ct);
            }
            if (!string.IsNullOrWhiteSpace(proxyUrl))
                handler.Proxy = CreateProxy(proxyUrl, proxyUsername, proxyPassword);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            sw.Stop();
            tls = response.RequestMessage?.RequestUri?.Scheme == "https" ? "TLS" : "HTTP";
            return new HttpProbeResult
            {
                Url = url,
                FinalUrl = response.RequestMessage?.RequestUri?.ToString(),
                SourceAddress = sourceAddress,
                CertificateError = certificateError,
                StatusCode = (int)response.StatusCode,
                ElapsedMs = sw.ElapsedMilliseconds,
                TlsProtocol = tls,
                CertificateSubject = subject,
                CertificateIssuer = issuer,
                CertificateNotAfter = notAfter
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new HttpProbeResult
            {
                Url = url,
                Error = certificateError is null ? ex.Message : $"TLS validation failed: {certificateError}",
                CertificateError = certificateError,
                SourceAddress = sourceAddress,
                CertificateSubject = subject,
                CertificateIssuer = issuer,
                CertificateNotAfter = notAfter,
                TlsProtocol = tls
            };
        }
    }

    internal static WebProxy CreateProxy(string url, string? username, string? password)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Use an HTTP(S) proxy URL and enter credentials in the separate username/password fields.");
        return new WebProxy(uri)
        {
            Credentials = string.IsNullOrEmpty(username) ? null : new NetworkCredential(username, password)
        };
    }

    public Task<DnsLookupResult> LookupDnsAsync(string server, string name, DnsRecordType type, int timeoutMs = 3000,
        string? sourceAddress = null, CancellationToken cancellationToken = default) =>
        new DnsLookupClient().QueryAsync(server, name, type, timeoutMs, sourceAddress, cancellationToken);

    public async Task<PingSample> QueryDnsAsync(string server, string name, int timeoutMs, string? sourceAddress = null, CancellationToken cancellationToken = default)
    {
        var result = await LookupDnsAsync(server, name, DnsRecordType.A, timeoutMs, sourceAddress, cancellationToken);
        return new PingSample { Sequence = 0, Success = result.Success, RoundtripMs = result.ElapsedMs,
            SourceBound = !string.IsNullOrWhiteSpace(sourceAddress),
            Status = result.Success ? string.Join(", ", result.Answers.Where(a => a.Type == "A").Select(a => a.Value)) : result.Error ?? result.Status };
    }

    private static async Task<IReadOnlyList<TraceHop>> RunProcessTraceAsync(
        string file, string[] args, Func<string, TraceHop?> parse, IProgress<TraceHop> progress, CancellationToken cancellationToken)
    {
        var hops = new List<TraceHop>();
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in args) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi);
        if (process is null) return hops;
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                var hop = parse(line);
                if (hop is null) continue;
                hops.Add(hop); progress.Report(hop);
            }
            await process.WaitForExitAsync(cancellationToken);
            var error = await errors;
            if (process.ExitCode != 0 && hops.Count == 0) throw new InvalidOperationException(error.Trim());
            return hops;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private static TraceHop? ParseUnix(string line)
    {
        var trim = line.Trim();
        if (trim.Length == 0 || trim.StartsWith("traceroute") || trim.StartsWith("tracepath")) return null;
        var parts = trim.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0].Trim('.'), out var ttl)) return null;
        var timedOut = trim.Contains('*') && parts.Length < 3;
        var address = parts.Length > 1 ? parts[1] : "*";
        var rtt = parts.Length > 2 ? string.Join(" ", parts.Skip(2).Take(2)) : "*";
        return new TraceHop { Ttl = ttl, Address = address, Rtt = rtt, TimedOut = timedOut || address == "*" };
    }

    private static TraceHop? ParseWindows(string line)
    {
        var trim = line.Trim();
        if (trim.Length == 0) return null;
        var parts = trim.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var ttl)) return null;
        var timedOut = trim.Contains("Request timed out", StringComparison.OrdinalIgnoreCase) || parts.Contains("*");
        var address = timedOut ? "*" : parts[^1];
        return new TraceHop { Ttl = ttl, Address = address, Rtt = timedOut ? "*" : parts[1], TimedOut = timedOut };
    }

    private static async Task<IReadOnlyList<TraceHop>> FallbackTtlPingAsync(string host, IProgress<TraceHop> progress, CancellationToken cancellationToken)
    {
        IPAddress address;
        try { address = (await Dns.GetHostAddressesAsync(host, cancellationToken))[0]; }
        catch (OperationCanceledException) { throw; }
        catch { return []; }

        var hops = new List<TraceHop>();
        using var ping = new Ping();
        var options = new PingOptions(1, true);
        var buffer = new byte[32];
        for (var ttl = 1; ttl <= 20; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.Ttl = ttl;
            try
            {
                var reply = await ping.SendPingAsync(address, 1000, buffer, options).WaitAsync(cancellationToken);
                var hop = new TraceHop
                {
                    Ttl = ttl,
                    Address = reply.Address is null || reply.Address.ToString() is "0.0.0.0" ? "*" : reply.Address.ToString(),
                    Rtt = reply.Status == IPStatus.TimedOut ? "*" : $"{reply.RoundtripTime} ms",
                    TimedOut = reply.Status == IPStatus.TimedOut
                };
                hops.Add(hop);
                progress.Report(hop);
                if (reply.Status == IPStatus.Success) break;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                var hop = new TraceHop { Ttl = ttl, Address = "*", Rtt = "*", TimedOut = true };
                hops.Add(hop);
                progress.Report(hop);
            }
        }

        return hops;
    }
}
