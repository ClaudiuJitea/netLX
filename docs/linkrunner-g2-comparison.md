# LinkRunner G2 comparison and implementation audit — PortStrider v1.3.0

Reviewed 22 October 2026. This audit follows the code from profiles and views through services, packet parsers, OS commands and stored results. It distinguishes implemented behavior, tested behavior, platform dependencies and missing capabilities. It does not establish certification or hardware interoperability.

The reference is NetAlly's [LinkRunner G2 user guide, May 29, 2019](https://www.netally.com/wp-content/uploads/2019/09/LinkRunnerG2_UserGuide_English.pdf), supplemented by its [G2 product page](https://www.netally.com/products/linkrunnerg2/). Page numbers below are the guide's printed page numbers. Features from newer LinkRunner models are not treated as G2 requirements.

## Assessment

PortStrider already covers much of a laptop-based Ethernet troubleshooting workflow. Its strongest areas are passive switch discovery, profile-driven connectivity checks, host capture, WiFi scan visualization and local results. Its largest software weakness was the reliability of conclusions: some operations could follow the wrong source, accept incomplete DNS answers, or continue after configuration failed. This release prioritizes those issues and adds reusable target editing and a direct DNS diagnostic tool.

A host NIC is not an electrical cable tester or PoE load. G2 hardware capabilities cannot be recreated by adding UI controls. PortStrider's supported link speeds, fiber modules, capture throughput and physical diagnostics depend on the host adapter and driver.

## Capability inventory

| Area / G2 reference | PortStrider v1.3 implementation | Scope and limitations |
| --- | --- | --- |
| AutoTest, pp. 48–62 | `AutoTestRunner`, `AutoTestViewModel`: link, identity, wired authentication, DHCP/addressing, VLAN, IPv6, gateway, DNS and user targets; parallel switch discovery and advertised PoE. | Configurable stop point and continuous ping/TCP exist. Pending cards and parallel discovery do not exactly reproduce G2 stop-after presentation. |
| Link settings, pp. 36–38, 53–54 | `LinkTelemetryService`, `EthtoolSettingsParser`: negotiated/local/partner speeds, duplex, MDI-X and downshift detection. | Detailed Linux telemetry requires ethtool and driver support. Windows exposes fewer details. Unknown driver values are not measurements. |
| Connect settings, pp. 36–40 | `InterfaceConfigService`: temporary MAC, speed/duplex, VLAN priority and static IPv4. | Linux only. Existing interface/address/route conflicts are refused. Rollback is best effort; process crashes and driver failures are not transactional. |
| Wired 802.1X, pp. 37–38, 56 | `WpaSupplicantDot1xService`: PEAP, TTLS, TLS and MD5 configuration. | Linux wpa_supplicant required. Enterprise interoperability and interaction with the host network manager need field tests. |
| DHCP, pp. 38–39, 57–58 | `DhcpTestService`, `DhcpPacket`, `DhcpTransport`: DORA, selected vendor options, transaction/MAC/server matching and timings. Existing usable IPv4 selects INFORM. | INFORM checks configuration acknowledgement, not acquisition/renewal. Unconfigured physical NICs remain managed by the OS; only owned temporary VLANs receive a test lease. |
| IPv6 addressing, p. 58 | `NetworkAdapterService`, `Ipv6Addressing`: global/ULA/link-local reporting and an IPv6 reachability probe. TCP and DNS also support IPv6 sources. | No DHCPv6 exchange or RA analysis. Linux ping binds the source; Windows ICMP is labeled OS-routed. Public-probe failure is a warning, not proof all IPv6 is broken. |
| DNS, p. 59 | `DnsLookupClient`, `DnsWire`: direct queries to up to four AutoTest servers with separate DNS/ICMP timings. New Tools lookup supports A, AAAA, CNAME, NS, MX, TXT and PTR with TTLs. | AutoTest resolves an A record. Tools can request AAAA. No DNSSEC validation, EDNS, DoH/DoT or iterative resolver. PTR requires a reverse-zone name. |
| Gateway/targets, pp. 40–41, 60–61 | Ping/TCP targets, continuous probes, plus HTTP/TLS checks. New profile UI adds/copies/removes/enables targets and validates endpoints. | HTTP checks response headers/status and certificate validity, not application body correctness. Hostname resolution uses the OS resolver. |
| Proxy settings, p. 40 | `ProbeService`, `ProfilesViewModel`: HTTP(S) proxy URL and challenge credentials for AutoTest HTTP targets. | Tools HTTP does not read profile proxy settings. The HTTP stack may use host proxy configuration if no explicit proxy is supplied. |
| Profiles/jobs, pp. 44–46 | `JsonProfileStore`, `ProfilesViewModel`: local reusable profiles, duplication/export and job/site labels in reports. | Import service exists but has no profile-import button. No shared profile distribution. Local passwords remain in clear-text JSON; exports omit proxy and 802.1X passwords. |
| Switch discovery, pp. 55, 64–65 | `SwitchDiscoveryService`, LLDP/CDP/EDP parsers: switch/port identity, management address, voice VLAN and advertised PoE. | Passive advertisements only; this is not a full network topology or SNMP inventory. Requires capture access and a matching NIC. |
| Port identification, p. 65 | `FlashPortAsync`: cycles link state to blink the switch LED. | Linux only; this interrupts the selected link. |
| VLAN monitor, pp. 79–80 | `VlanMonitorService`, `VlanTrafficCounter`: top nine tagged VLANs plus Other VLANs and Untagged rows; priority tag 0 is distinct. | Captured-byte percentages, outer tag for QinQ. Offloads, NIC visibility and capture loss affect results. This is not switch-wide utilization. |
| Packet capture, pp. 81–84 | `PacketCaptureService`: streaming PCAP, BPF filter, snap length and a 2 GB cap. | Host-dependent performance. Capture-device selection is exact/unique. OS/NIC tag stripping can hide VLANs. No line-rate guarantee. |
| Reflector, pp. 76–78 | `ReflectorService`: MAC or MAC/IP/port swapping and reinjection. | The NetAlly mode uses a UDP filter heuristic. Compatibility with a physical NetAlly performance tester is unverified. It is not an iperf server. |
| Cable/fiber, pp. 66–70 | `CableTestService`, `SfpDiagnosticsService`: driver-exposed TDR and SFP EEPROM/DDM; link/PHY visualization. | No full wiremap, split-pair certification, remote ID detection or tone generation. Many NICs expose neither TDR nor module diagnostics. |
| Loaded PoE, pp. 50–52 | Advertised PoE values decoded from LLDP/CDP only. | No voltage/current/load measurement or TruePower equivalent; dedicated hardware required. |
| Results/cloud, pp. 63, 71–74 | `JsonReportStore`, `ReportsViewModel`: local JSON/CSV/PDF, ZIP bundles, notes, attachment files, search and duplication. | No Link-Live claiming/upload/team management. PDF is currently a short ASCII summary limited to 48 lines; bundle import does not restore embedded attachments. JSON is the complete structured export. |
| Device workflows, pp. 85–98 | Manual GitHub downloads and file-based image attachments. | No firmware/device-management equivalent, in-app update flow, camera capture or localization. |

### Additional host tools

These are useful PortStrider features; their presence does not establish G2 equivalence:

- `WifiService`, `IwScanParser`, `WindowsWifi`: Linux iw and Windows WLAN access-point scans, channel footprints, overlap graphics and RSSI history. This is not an RF spectrum analyzer. Windows channel-width/security detail is limited; OS location permission may be required.
- `ProbeService`: traceroute with bounded arguments and cancellable child processes. Traceroute follows OS routing.
- Tools port probes: up to 64 distinct TCP ports, eight concurrent connections, with timeout/error results.
- `IperfService`: external iperf3 TCP client/server. OS routing; server listens on all interfaces. UDP/jitter/reverse mode and source-selection controls remain absent.
- `NeighborService`: host neighbor/ARP cache view, not active subnet discovery.
- `DependencyManager` and `CapabilityService`: prerequisite checks, repair actions and platform capability descriptions. Tool presence alone does not prove hardware support.

## Findings fixed in v1.3

| Observed before this release | Change and evidence |
| --- | --- |
| HTTP certificate validation could be bypassed if the URL contained `1.1.1.1`. | Accept only `SslPolicyErrors.None`, report certificate errors and final URL. A local self-signed TLS server verifies the old URL bypass no longer works. |
| Linux ICMP ignored the selected source; TCP sockets assumed IPv4; HTTP did not bind a source. | Linux iputils `ping -I`, family-aware TCP sockets and HTTP `ConnectCallback`. Local servers observe `127.0.0.2` as the source; IPv6 loopback TCP is tested. Windows ICMP remains explicitly degraded. |
| DNS validation was incomplete, empty success replies could pass, and truncation had no TCP retry. | Connected UDP validates the peer; parser validates ID, response/opcode, question/type/class and bounds. Only the relevant answer/CNAME chain counts. TC triggers TCP using the same source and deadline. NODATA/NXDOMAIN do not pass. |
| Target configuration existed in the model but was missing from the profile editor. | Add/copy/remove/enable/edit ping, TCP and HTTP targets with endpoint/port validation and persistence through the existing store. Headless UI checks exercise actual Copy/Remove bindings. |
| Capture selection allowed fuzzy or default-device matches. | All capture services share exact-name or unique exact-alias resolution. Missing or ambiguous names fail instead of selecting another NIC. |
| VLAN 0 and untagged traffic were conflated; traffic outside the top nine was lost in presentation. | Separate priority-tagged/untagged identities; aggregate remaining VLANs into Other VLANs; denominator includes all captured bytes. UI updates are throttled. |
| VLAN setup deleted an existing name; static settings used replacement; partial failures could evade rollback. | Create without pre-deleting; add rather than replace addresses/routes; register cleanup before multi-step changes; preserve original forced mode; reject invalid masks; stop AutoTest on incomplete settings, missing effective adapter or unapplied VLAN lease. Failure/cancellation tests verify cleanup behavior. |
| Tools could continue running after navigation or adapter changes. | Shared cancellation plus Stop tests, source capture at operation start, child-process cleanup and bounded concurrent port tests. |
| Profile export removed only the proxy password. | Export also omits the wired 802.1X password/key passphrase. Local serialization retains both. |

The retained DHCP fix uses INFORM when an IPv4 address is already configured. That distinction follows [RFC 2131 §§3.4 and 4.4.3](https://www.rfc-editor.org/rfc/rfc2131). DNS matching follows the checks in [RFC 5452 §9](https://www.rfc-editor.org/rfc/rfc5452); TCP fallback follows [RFC 7766](https://www.rfc-editor.org/rfc/rfc7766). HTTP binding uses the [.NET connection callback](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-8.0); Linux source selection uses [iputils ping](https://github.com/iputils/iputils/blob/master/doc/ping.xml).

## What source binding proves

Binding an IP address constrains the packet's source. It does not isolate the application from host routes, policy routing, proxy settings or hostname resolution. This release does not use network namespaces or bind every operation to a kernel device. On dual-stack adapters, hostnames prefer the adapter's IPv4 source; IPv6 literals select IPv6. A literal can be used to investigate the other family explicitly.

Direct DNS, TCP and HTTP fail on an incompatible selected source. Linux ICMP requires iputils-ping and does not silently fall back to OS-routed ICMP. Windows ICMP can still run, but AutoTest marks an otherwise successful result as a warning when source enforcement is unavailable. Traceroute and iperf are labeled OS-routed.

## Remaining work, ordered by practical value

1. **Host integration:** Windows source-bound ICMP and configuration backends; per-interface hostname resolution; predictable handling of network-manager changes; surfaced rollback failures and recovery after crashes.
2. **IPv6 depth:** scoped link-local server selection, per-router tests, RA/SLAAC information, DHCPv6 and explicit address-family selection for hostname targets. Link-local DNS servers are currently excluded from AutoTest.
3. **Result portability:** multi-page Unicode PDF, attachment extraction and path remapping on bundle import, profile-import UI, protected local credential storage. Current PDF/bundle limits predate this release and remain unresolved.
4. **Test workflows:** exact stop-after card presentation, stronger reflector interoperability, iperf UDP/jitter/reverse/source options, application-content assertions and configurable IPv6 probe destinations.
5. **Shared workflows:** supported Link-Live or independent cloud integration, camera attachments, localization and a signed update workflow.
6. **Hardware work:** only add loaded PoE, full wiremap/remote IDs or tone through a real supported hardware interface. Do not infer these from LLDP or a successful link.

## Validation and field-test boundary

The release suite covers the existing DHCP INFORM/DORA paths, packet matching/checksums, proxy authentication, Linux parsers and the new protocol/configuration regressions. DNS tests include all seven record types, malformed/truncated messages, mismatched questions, wrong senders, TCP fallback, negative answers and cancellation. Network tests use local UDP/TCP/TLS servers and loopback addresses; configuration tests use a fake command runner and do not alter the development machine's networking.

Headless Avalonia renders the DNS and target screens and checks target-command bindings. Release builds produce Linux and Windows x64/arm64 binaries; checksums accompany each release. The release notes record the final test count and build results.

Still required in a lab: physical VLAN/DHCP/switch/802.1X interoperability, actual rollback under network-manager control, Windows and arm64 execution, multi-NIC routing behavior, capture drop/load measurement, and a NetAlly reflector peer. Passing local tests and cross-compiling do not establish these results.
