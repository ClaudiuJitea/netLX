using System.Diagnostics;
using System.Net;
using NetLX.Core.Enums;
using NetLX.Core.Models;
using NetLX.Core.Services;

namespace NetLX.Infrastructure.Probes;

/// <summary>
/// Profile-driven AutoTest modelled on the LinkRunner G2 card sequence:
/// interface configuration → link → 802.1X → VLAN → addressing (DHCP DORA or static) → gateway → DNS → targets.
/// Switch discovery, VLAN observation and PoE run in parallel in the view model because they share the capture device.
/// </summary>
public sealed class AutoTestRunner : IAutoTestService
{
    public const string StepConfig = "cfg";
    public const string StepLink = "link";
    public const string StepIdentity = "id";
    public const string StepDot1x = "8021x";
    public const string StepVlan = "vlan";
    public const string StepAddressing = "addr";
    public const string StepIpv6 = "ipv6";
    public const string StepDhcp = "dhcp";
    public const string StepGateway = "gw";
    public const string StepDns = "dns";
    public const string StepPoe = "poe";

    private readonly IProbeService _probes;
    private readonly IDhcpTestService _dhcp;
    private readonly ILinkTelemetryService _telemetry;
    private readonly IInterfaceConfigService _ifconfig;
    private readonly IDot1xService _dot1x;
    private readonly IAdapterService _adapters;

    public AutoTestRunner(
        IProbeService probes,
        IDhcpTestService dhcp,
        ILinkTelemetryService telemetry,
        IInterfaceConfigService ifconfig,
        IDot1xService dot1x,
        IAdapterService adapters)
    {
        _probes = probes;
        _dhcp = dhcp;
        _telemetry = telemetry;
        _ifconfig = ifconfig;
        _dot1x = dot1x;
        _adapters = adapters;
    }

    public async Task<SessionReport> ExecuteAsync(AdapterInfo adapter, TestProfile profile, IProgress<TestStepUpdate> progress, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        var steps = new List<TestStepUpdate>();
        var targets = new List<TargetTestResult>();
        var pingCount = Math.Clamp(profile.PingCount <= 0 ? 3 : profile.PingCount, 1, 10);

        LinkTelemetry? link = null;
        DhcpTestResult? dhcp = null;
        DnsTestResult? dns = null;
        VlanTestResult? vlan = null;
        Dot1xResult? dot1x = null;
        InterfaceConfigResult? cfgResult = null;
        InterfaceConfigSession? cfg = null;
        Dot1xSession? dot1xSession = null;
        var effective = adapter;

        void Report(string id, string title, TestStatus status, string details, string? host = null, int? port = null)
        {
            var update = new TestStepUpdate { Id = id, Title = title, Status = status, Details = details, ContinuousHost = host, ContinuousPort = port };
            steps.RemoveAll(s => s.Id == id);
            steps.Add(update);
            progress.Report(update);
        }

        bool StopAfter(string id) => !string.IsNullOrWhiteSpace(profile.StopAfterStepId)
                                     && string.Equals(profile.StopAfterStepId, id, StringComparison.OrdinalIgnoreCase);

        SessionReport Build(string? overall = null) => new()
        {
            Id = Guid.NewGuid(),
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            AdapterName = adapter.Name,
            ProfileName = profile.Name,
            Overall = overall ?? Overall(steps),
            Ipv4 = effective.Ipv4 ?? "",
            Ipv6 = effective.Ipv6 ?? "",
            Ipv6LinkLocal = effective.Ipv6LinkLocal ?? "",
            Gateway = effective.Gateway ?? dhcp?.Gateway ?? "",
            Steps = steps.ToArray(),
            Targets = targets.ToArray(),
            Dhcp = dhcp,
            Link = link,
            Dns = dns,
            Vlan = vlan,
            Dot1x = dot1x,
            InterfaceConfig = cfgResult
        };

        try
        {
            // ── Interface configuration (MAC / forced mode / VLAN / static) ───────────────────────────────
            if (_ifconfig.RequiresChanges(profile))
            {
                Report(StepConfig, "Interface configuration", TestStatus.Running, "Applying profile connect settings…");
                var log = new InlineProgress(m => Report(StepConfig, "Interface configuration", TestStatus.Running, m));
                cfg = await _ifconfig.ApplyAsync(adapter, profile, log, cancellationToken);
                cfgResult = cfg.Result;
                Report(StepConfig, "Interface configuration", cfg.Result.Status, cfg.Result.Summary);
                if (cfg.Result.Status != TestStatus.Pass || (profile.VlanId is > 0 && !cfg.IsSubInterface))
                {
                    Report(StepConfig, "Interface configuration", TestStatus.Fail,
                        cfg.Result.Summary + " · requested settings were not fully applied; test stopped");
                    return Build();
                }
                var configured = _adapters.Find(cfg.EffectiveInterface);
                if (configured is null)
                {
                    Report(StepConfig, "Interface configuration", TestStatus.Fail,
                        $"Cannot read configured interface {cfg.EffectiveInterface}; test stopped.");
                    return Build();
                }
                effective = configured;
            }

            // ── Physical link ─────────────────────────────────────────────────────────────────────────────
            Report(StepLink, "Physical link", TestStatus.Running, "Reading PHY / ethtool…");
            var baseNow = _adapters.Find(adapter.Name) ?? adapter;
            if (!baseNow.IsUp)
            {
                Report(StepLink, "Physical link", TestStatus.Fail, "No link — cable unplugged or NIC disabled");
                return Build("FAIL");
            }

            link = await _telemetry.ReadAsync(baseNow, cancellationToken);
            var (linkStatus, linkDetails) = DescribeLink(link, profile, baseNow);
            Report(StepLink, "Physical link", linkStatus, linkDetails);
            if (StopAfter(StepLink)) return Build();

            Report(StepIdentity, "Interface identity", TestStatus.Pass,
                $"{effective.MacAddress} · {effective.Media} · {effective.Description}"
                + (cfg is { IsSubInterface: true } ? $" · testing on {effective.Name}" : ""));

            // ── 802.1X ────────────────────────────────────────────────────────────────────────────────────
            if (profile.Enable8021X)
            {
                Report(StepDot1x, "802.1X authentication", TestStatus.Running, $"Starting supplicant ({profile.EapType.ToString().ToUpperInvariant()})…");
                var log = new Progress<string>(m =>
                {
                    if (m.Contains("CTRL-EVENT", StringComparison.Ordinal))
                        Report(StepDot1x, "802.1X authentication", TestStatus.Running, Short(m));
                });
                dot1xSession = await _dot1x.AuthenticateAsync(adapter.Name, profile, TimeSpan.FromSeconds(25), log, cancellationToken);
                dot1x = dot1xSession.Result;
                Report(StepDot1x, "802.1X authentication", dot1x.Status, dot1x.Summary);
                if (StopAfter(StepDot1x)) return Build();
            }

            effective = _adapters.Find(effective.Name) ?? effective;

            // ── Addressing / DHCP ─────────────────────────────────────────────────────────────────────────
            if (profile.AddressMode == AddressMode.Dhcp)
            {
                Report(StepDhcp, "DHCP", TestStatus.Running, $"Checking DHCP on {effective.Name}…");
                dhcp = await _dhcp.DiscoverAsync(effective, profile, allowRenew: false, cancellationToken);
                Report(StepDhcp, "DHCP", dhcp.Status, DescribeDhcp(dhcp));

                // On a temporary VLAN sub-interface nobody else will configure the lease, so we do — otherwise the
                // gateway/DNS/target steps would leave through the untagged interface.
                if (cfg is { IsSubInterface: true } && dhcp.LeaseAcquired && dhcp.OfferedIp is not null)
                {
                    var error = await _ifconfig.ApplyAddressAsync(cfg, dhcp.OfferedIp, dhcp.Subnet, FirstAddress(dhcp.Gateway), cancellationToken);
                    if (error is not null)
                    {
                        Report(StepDhcp, "DHCP", TestStatus.Fail, DescribeDhcp(dhcp) + $" · lease not fully applied: {error}; test stopped");
                        return Build();
                    }
                    effective = _adapters.Find(cfg.EffectiveInterface) ?? effective;
                }
            }
            else
            {
                Report(StepDhcp, "DHCP", TestStatus.Skipped, "Profile uses a static address.");
            }

            effective = _adapters.Find(effective.Name) ?? effective;
            Report(StepAddressing, "Addressing", TestStatus.Running, "Reading IPv4 / IPv6…");
            var ipv4 = effective.Ipv4;
            if (string.IsNullOrWhiteSpace(ipv4))
            {
                var why = profile.AddressMode == AddressMode.Dhcp
                    ? (dhcp?.LeaseAcquired == true ? $"Server offered {dhcp.OfferedIp} but the OS has no lease on {effective.Name}" : "No IPv4 address and no DHCP lease")
                    : "Static mode but no address configured";
                Report(StepAddressing, "Addressing", TestStatus.Warning, why);
            }
            else
            {
                var mode = profile.AddressMode == AddressMode.Static ? "static" : effective.DhcpEnabled ? "DHCP" : "OS";
                var dnsList = DnsServers(effective, dhcp, profile);
                Report(StepAddressing, "Addressing", TestStatus.Pass,
                    $"{ipv4}/{effective.SubnetMask ?? dhcp?.Subnet ?? "?"} ({mode}) · DNS {(dnsList.Count > 0 ? string.Join(", ", dnsList) : "none")}");
            }

            if (StopAfter(StepDhcp) || StopAfter(StepAddressing)) return Build();

            // ── VLAN card (configured) — the "Seen / VIDs" part is appended by the view model from discovery ─
            if (profile.VlanId is > 0)
            {
                var sub = cfg is { IsSubInterface: true };
                var status = !sub ? (_ifconfig.IsSupported ? TestStatus.Fail : TestStatus.Warning)
                    : dhcp is { LeaseAcquired: true } ? TestStatus.Pass
                    : dhcp is { Status: TestStatus.Fail } ? TestStatus.Fail
                    : TestStatus.Warning;
                var summary = !sub
                    ? $"VLAN {profile.VlanId} requested but no tagged sub-interface could be created ({cfg?.Result.Summary ?? "unsupported here"})"
                    : dhcp is { LeaseAcquired: true }
                        ? $"VID {profile.VlanId} · PRI {profile.VlanPriority} · DHCP lease {dhcp.OfferedIp} received on the tagged interface"
                        : $"VID {profile.VlanId} · PRI {profile.VlanPriority} · tagged interface up, {(dhcp?.Summary ?? "no DHCP attempt")}";
                vlan = new VlanTestResult
                {
                    ConfiguredVlan = profile.VlanId.Value,
                    Priority = profile.VlanPriority,
                    InterfaceName = cfg?.EffectiveInterface,
                    Status = status,
                    Summary = summary
                };
                Report(StepVlan, "VLAN", status, summary);
                if (StopAfter(StepVlan)) return Build();
            }

            if (effective.Ipv4 is null && effective.Ipv6 is null && effective.Ipv6LinkLocal is null)
            {
                Report(StepAddressing, "Addressing", TestStatus.Fail, "Selected adapter has no configured IP address; routed probes were not sent.");
                return Build();
            }

            // ── IPv6 ──────────────────────────────────────────────────────────────────────────────────────
            if (profile.EnableIpv6)
            {
                Report(StepIpv6, "IPv6 connectivity", TestStatus.Running, "Checking IPv6…");
                var addressing = Ipv6Addressing.Describe(effective);
                if (string.IsNullOrWhiteSpace(effective.Ipv6))
                    Report(StepIpv6, "IPv6 connectivity", effective.Ipv6LinkLocal is null ? TestStatus.Skipped : TestStatus.Warning,
                        addressing + " · global reachability not tested");
                else
                {
                    var v6 = await _probes.PingOnceAsync("2606:4700:4700::1111", 1500, effective.Ipv6, cancellationToken);
                    Report(StepIpv6, "IPv6 connectivity", v6.Success && v6.SourceBound ? TestStatus.Pass : TestStatus.Warning,
                        v6.Success ? $"{addressing} · RTT {v6.RoundtripMs} ms · {v6.Status}" : $"{addressing} · {v6.Status}",
                        "2606:4700:4700::1111");
                }
            }

            // ── Gateway ───────────────────────────────────────────────────────────────────────────────────
            Report(StepGateway, "Default gateway", TestStatus.Running, "Probing gateway…");
            var gateway = effective.Gateway ?? FirstAddress(dhcp?.Gateway) ?? profile.StaticGateway ?? "";
            if (string.IsNullOrWhiteSpace(gateway))
            {
                Report(StepGateway, "Default gateway", TestStatus.Warning, "No default gateway on this NIC");
            }
            else
            {
                var series = await PingSeriesAsync(gateway, pingCount, effective.SourceFor(gateway), cancellationToken);
                Report(StepGateway, "Default gateway", series.Status, $"{gateway} · {series.Details}", gateway);
            }

            if (StopAfter(StepGateway)) return Build();

            // ── DNS (each server, N queries) ──────────────────────────────────────────────────────────────
            Report(StepDns, "DNS servers", TestStatus.Running, $"Querying {profile.DnsQueryName}…");
            dns = await DnsTestAsync(DnsServers(effective, dhcp, profile), profile.DnsQueryName, pingCount, effective, cancellationToken);
            Report(StepDns, "DNS servers", dns.Status, dns.Summary);
            if (StopAfter(StepDns)) return Build();

            // ── User targets ──────────────────────────────────────────────────────────────────────────────
            foreach (var target in profile.Targets.Where(t => t.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var host = ResolveTargetHost(target, effective, gateway);
                if (string.IsNullOrWhiteSpace(host) && target.Kind != TargetKind.Http)
                    continue;

                var id = $"target-{target.Id}";
                Report(id, target.Label, TestStatus.Running, target.Kind switch
                {
                    TargetKind.Ping => $"Ping {host}",
                    TargetKind.TcpPort => $"TCP {host}:{target.Port}",
                    _ => target.Url
                });

                var targetDestination = target.Kind == TargetKind.Http ? target.Url : host;
                if (effective.SourceFor(targetDestination) is null)
                {
                    var detail = "Selected adapter has no source address for this target's address family.";
                    Report(id, target.Label, TestStatus.Fail, detail);
                    targets.Add(new TargetTestResult { TargetId = target.Id, Label = target.Label, Status = TestStatus.Fail, Details = detail });
                    if (StopAfter(id) || StopAfter(target.Id)) return Build();
                    continue;
                }
                var sw = Stopwatch.StartNew();
                switch (target.Kind)
                {
                    case TargetKind.Ping:
                    {
                        var series = await PingSeriesAsync(host, pingCount, effective.SourceFor(host), cancellationToken);
                        sw.Stop();
                        var details = $"{host} · {series.Details}";
                        Report(id, target.Label, series.Status, details, host);
                        targets.Add(new TargetTestResult { TargetId = target.Id, Label = target.Label, Status = series.Status, Details = details, ElapsedMs = sw.ElapsedMilliseconds });
                        break;
                    }
                    case TargetKind.TcpPort:
                    {
                        var times = new List<string>();
                        var opens = 0;
                        for (var i = 0; i < pingCount; i++)
                        {
                            var port = await _probes.ProbePortAsync(host, target.Port, 1200, effective.SourceFor(host), cancellationToken);
                            if (port.Open) opens++;
                            times.Add(port.Open ? $"{port.ElapsedMs}" : "×");
                        }

                        sw.Stop();
                        var status = opens == pingCount ? TestStatus.Pass : opens > 0 ? TestStatus.Warning : TestStatus.Fail;
                        var details = $"{host}:{target.Port} · {opens}/{pingCount} open · {string.Join(" / ", times)} ms";
                        Report(id, target.Label, status, details, host, target.Port);
                        targets.Add(new TargetTestResult { TargetId = target.Id, Label = target.Label, Status = status, Details = details, ElapsedMs = sw.ElapsedMilliseconds });
                        break;
                    }
                    case TargetKind.Http:
                    {
                        var url = string.IsNullOrWhiteSpace(target.Url) ? $"https://{host}" : target.Url;
                        var http = await _probes.ProbeHttpAsync(url, profile.ProxyUrl, cancellationToken, profile.ProxyUsername, profile.ProxyPassword, effective.SourceFor(url));
                        sw.Stop();
                        var status = http.Success ? TestStatus.Pass : TestStatus.Fail;
                        var details = http.Error ?? $"HTTP {http.StatusCode} in {http.ElapsedMs} ms"
                            + (http.CertificateNotAfter is { } exp ? $" · cert expires {exp:yyyy-MM-dd}" : "");
                        Report(id, target.Label, status, details);
                        targets.Add(new TargetTestResult { TargetId = target.Id, Label = target.Label, Status = status, Details = details, ElapsedMs = sw.ElapsedMilliseconds });
                        break;
                    }
                }

                if (StopAfter(id) || StopAfter(target.Id)) return Build();
            }

            return Build();
        }
        finally
        {
            if (dot1xSession is not null)
            {
                try { await dot1xSession.StopAsync(); } catch { /* ignore */ }
            }

            if (cfg is not null)
            {
                try { await cfg.RestoreAsync(CancellationToken.None); } catch { /* best effort */ }
            }
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────

    private static (TestStatus, string) DescribeLink(LinkTelemetry link, TestProfile profile, AdapterInfo adapter)
    {
        var parts = new List<string>
        {
            $"{link.ActualSpeed}{(link.Duplex is not ("—" or "") ? $" {link.Duplex}-duplex" : "")}"
        };
        if (link.AdvertisedSpeed != "—") parts.Add($"advertised {link.AdvertisedSpeed}{(link.AdvertisedDuplex is not ("—" or "") ? $" {link.AdvertisedDuplex}" : "")}");
        if (link.PartnerSpeed != "—") parts.Add($"partner {link.PartnerSpeed}");
        if (link.MdiMode != "—") parts.Add(link.MdiMode);
        if (link.AutoNegotiation != "—") parts.Add($"autoneg {link.AutoNegotiation}");
        parts.Add($"MTU {adapter.Mtu}");

        var status = TestStatus.Pass;
        var expected = ExpectedMbps(profile.LinkSpeed);
        if (expected > 0 && link.ActualSpeedMbps > 0 && link.ActualSpeedMbps != expected)
        {
            status = TestStatus.Warning;
            parts.Insert(0, $"expected {EthtoolLabel(expected)}");
        }
        else if (profile.LinkDuplex != DuplexSetting.Auto && !string.Equals(link.Duplex, profile.LinkDuplex.ToString(), StringComparison.OrdinalIgnoreCase) && link.Duplex is not ("—" or ""))
        {
            status = TestStatus.Warning;
            parts.Insert(0, $"expected {profile.LinkDuplex}-duplex");
        }
        else if (link.Downshift)
        {
            status = TestStatus.Warning;
            parts.Insert(0, "downshift");
        }

        return (status, string.Join(" · ", parts));
    }

    private static long ExpectedMbps(LinkSpeedSetting s) => s switch
    {
        LinkSpeedSetting.Mbps10 => 10,
        LinkSpeedSetting.Mbps100 => 100,
        LinkSpeedSetting.Mbps1000 => 1000,
        LinkSpeedSetting.Mbps2500 => 2500,
        LinkSpeedSetting.Mbps5000 => 5000,
        LinkSpeedSetting.Mbps10000 => 10000,
        _ => 0
    };

    private static string EthtoolLabel(long mbps) => mbps >= 1000 ? $"{mbps / 1000d:0.#} Gbps" : $"{mbps} Mbps";

    private static string DescribeDhcp(DhcpTestResult dhcp)
    {
        if (!dhcp.Supported) return dhcp.Summary;
        var bits = new List<string> { dhcp.Summary };
        if (dhcp.Subnet is not null) bits.Add($"mask {dhcp.Subnet}");
        if (dhcp.LeaseTime is not null) bits.Add($"lease {dhcp.LeaseTime}");
        if (dhcp.Option150 is not null) bits.Add($"opt150 {dhcp.Option150}");
        if (dhcp.Option43 is not null) bits.Add($"opt43 {Short(dhcp.Option43)}");
        if (dhcp.Option60 is not null) bits.Add($"opt60 {dhcp.Option60}");
        if (dhcp.DomainName is not null) bits.Add(dhcp.DomainName);
        return string.Join(" · ", bits);
    }

    private sealed record Series(TestStatus Status, string Details);
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private async Task<Series> PingSeriesAsync(string host, int count, string? source, CancellationToken cancellationToken)
    {
        if (source is null) return new Series(TestStatus.Fail, "Selected adapter has no compatible source address.");
        var rtts = new List<string>();
        var ok = 0;
        var sourceVerified = true;
        string? lastError = null;
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = await _probes.PingOnceAsync(host, 1200, source, cancellationToken);
            if (!string.IsNullOrEmpty(source) && !sample.SourceBound) sourceVerified = false;
            if (sample.Success)
            {
                ok++;
                rtts.Add(sample.RoundtripMs.ToString());
            }
            else
            {
                rtts.Add("×");
                lastError = sample.Status;
            }

            if (i < count - 1) await Task.Delay(150, cancellationToken);
        }

        var status = ok == count && sourceVerified ? TestStatus.Pass : ok > 0 ? TestStatus.Warning : TestStatus.Fail;
        var details = $"{ok}/{count} replies · {string.Join(" / ", rtts)} ms";
        if (!sourceVerified) details += " · OS routing; selected source not enforced";
        if (ok == 0 && lastError is not null) details += $" ({Short(lastError)})";
        return new Series(status, details);
    }

    private async Task<DnsTestResult> DnsTestAsync(IReadOnlyList<string> servers, string name, int count, AdapterInfo adapter, CancellationToken cancellationToken)
    {
        if (servers.Count == 0)
        {
            // Nothing learned from the NIC or DHCP — fall back to the OS resolver so the card still says something useful.
            try
            {
                var sw = Stopwatch.StartNew();
                var addrs = await Dns.GetHostAddressesAsync(name, cancellationToken);
                sw.Stop();
                return new DnsTestResult
                {
                    Status = TestStatus.Warning,
                    QueryName = name,
                    Summary = $"No DNS servers on adapter · OS resolver answered {addrs.FirstOrDefault()} in {sw.ElapsedMilliseconds} ms"
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new DnsTestResult { Status = TestStatus.Fail, QueryName = name, Summary = $"No DNS servers and OS resolver failed: {Short(ex.Message)}" };
            }
        }

        var results = new List<DnsServerResult>();
        foreach (var server in servers.Take(4))
        {
            var rtts = new List<long?>();
            var pingRtts = new List<long?>();
            var source = adapter.SourceFor(server);
            if (source is null)
            {
                results.Add(new DnsServerResult { Server = server, RoundtripsMs = Enumerable.Repeat<long?>(null, count).ToArray(), Error = "Selected adapter has no compatible source address" });
                continue;
            }
            string? resolved = null, error = null;
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = _probes.QueryDnsAsync(server, name, 1500, source, cancellationToken);
                var ping = _probes.PingOnceAsync(server, 1200, source, cancellationToken);
                await Task.WhenAll(query, ping);
                var sample = await query;
                var echo = await ping;
                pingRtts.Add(echo.Success ? echo.RoundtripMs : null);
                if (sample.Success)
                {
                    rtts.Add(sample.RoundtripMs);
                    resolved ??= sample.Status;
                }
                else
                {
                    rtts.Add(null);
                    error = sample.Status;
                }
            }

            results.Add(new DnsServerResult { Server = server, RoundtripsMs = rtts, PingRoundtripsMs = pingRtts, ResolvedAddress = resolved, Error = error });
        }

        var good = results.Count(r => r.RoundtripsMs.All(x => x.HasValue));
        var any = results.Count(r => r.Success);
        var status = good == results.Count ? TestStatus.Pass : any > 0 ? TestStatus.Warning : TestStatus.Fail;
        var summary = string.Join(" · ", results.Select(r =>
            $"{r.Server}: DNS {string.Join("/", r.RoundtripsMs.Select(x => x.HasValue ? x.Value.ToString() : "×"))} ms; ICMP {string.Join("/", r.PingRoundtripsMs.Select(x => x.HasValue ? x.Value.ToString() : "×"))} ms"
            + (r.Success ? "" : $" ({Short(r.Error ?? "no answer")})")));
        var answer = results.FirstOrDefault(r => r.ResolvedAddress is not null)?.ResolvedAddress;
        if (answer is not null) summary = $"{name} → {answer} · " + summary;

        return new DnsTestResult { Status = status, QueryName = name, Summary = summary, Servers = results };
    }

    private static IReadOnlyList<string> DnsServers(AdapterInfo adapter, DhcpTestResult? dhcp, TestProfile profile)
    {
        var list = new List<string>();
        if (profile.AddressMode == AddressMode.Static && !string.IsNullOrWhiteSpace(profile.StaticDns))
            list.AddRange(SplitAddresses(profile.StaticDns));
        if (dhcp?.Dns is not null) list.AddRange(SplitAddresses(dhcp.Dns));
        list.AddRange(adapter.DnsServers.Where(d => !d.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)));
        return list.Where(s => IPAddress.TryParse(s, out _)).Distinct().Take(4).ToArray();
    }

    private static IEnumerable<string> SplitAddresses(string text) =>
        text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? FirstAddress(string? list) =>
        string.IsNullOrWhiteSpace(list) ? null : SplitAddresses(list).FirstOrDefault();

    private static string ResolveTargetHost(TestTarget target, AdapterInfo adapter, string gateway) =>
        target.Host switch
        {
            "" or "*" when target.Id == "gw" => gateway,
            "" or "*" => adapter.Gateway ?? adapter.Ipv4 ?? "",
            _ => target.Host
        };

    private static string Overall(IEnumerable<TestStepUpdate> steps)
    {
        var list = steps.ToArray();
        if (list.Any(s => s.Status == TestStatus.Fail)) return "FAIL";
        if (list.Any(s => s.Status == TestStatus.Warning)) return "WARN";
        if (list.All(s => s.Status is TestStatus.Pass or TestStatus.Skipped)) return "PASS";
        return "RUNNING";
    }

    private static string Short(string message) => message.Length <= 140 ? message : message[..137] + "…";
}
