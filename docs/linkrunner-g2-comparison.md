# LinkRunner G2 comparison — PortStrider v1.2.0

Reviewed 22 October 2026 against the [NetAlly G2 user guide](https://www.netally.com/wp-content/uploads/2019/09/LinkRunnerG2_UserGuide_English.pdf) and [G2 product capabilities](https://www.netally.com/products/linkrunnerg2/). This compares implemented behavior, not just feature names. G2 firmware generations differ; this is not a claim of certified compatibility.

## Software gaps addressed in this release

| Feature | Before v1.2 | v1.2 behavior / implementation |
| --- | --- | --- |
| Proxy authentication (guide p. 40) | Proxy URL only | Profile username/password, HTTP proxy challenge authentication, masked editor, password omitted from profile exports. `Profile`, `ProfilesViewModel`, `ProbeService`. |
| IPv6 address visibility | Link-local addresses discarded; link-local-only adapters appeared unconfigured | Link-local address retained in adapter snapshots and reports; AutoTest labels link-local, global, and unique-local addresses. `NetworkAdapterService`, `Ipv6Addressing`, `AutoTestRunner`. |
| DHCP testing on an already configured host | Always attempted DORA through UDP/68, potentially conflicting with the OS DHCP client | INFORM/ACK checks server configuration without replacing the existing address. Unconfigured adapters retain DORA. Dedicated pcap handle supports concurrent discovery. `DhcpTestService`, `DhcpTransport`. |

The DHCP change adapts testing to a shared laptop NIC. It does not claim that an INFORM acknowledgement proves lease acquisition or renewal. See [RFC 2131 sections 3.4 and 4.4.3](https://www.rfc-editor.org/rfc/rfc2131) for this distinction.

## Already implemented

| G2 capability | PortStrider implementation and limits |
| --- | --- |
| Link speed/duplex and downshift | `LinkTelemetryService` and ethtool parser; advertised modes and MDI-X depend on Linux drivers. Windows reports less telemetry. |
| Switch identification / voice VLAN | LLDP, CDP, EDP, LLDP-MED and observed VLAN tags in the discovery services; only information actually advertised on the port is available. |
| VLAN tagging, MAC override, static IPv4, forced link settings | Temporary Linux interface changes with rollback in `InterfaceConfigService`; no Windows configuration backend. |
| Wired 802.1X | Linux wpa_supplicant integration, PEAP/TTLS/TLS/MD5. |
| DHCP vendor options | Options 43/60/150 and configuration fields in `DhcpPacket`; DORA timing remains available when acquiring a lease. |
| DNS, gateway and user targets | AutoTest DNS queries, ping, TCP and HTTP targets; continuous ping/TCP and automatic run on link-up already exist. |
| VLAN traffic view / packet capture | Top-nine VLAN view and streaming PCAP with filters and a 2 GB cap. Capture speed depends on the host and NIC; line-rate performance is not guaranteed. |
| Reflector | MAC/IP/port swapping exists. The “NetAlly” filter is a UDP heuristic; interoperability with NetAlly performance testers has not been certified. |
| Results, notes and attachments | Local JSON/CSV/PDF/ZIP reporting, search and file attachments; no vendor cloud service. |

## Remaining gaps

| Gap | Status / follow-up |
| --- | --- |
| Link-Live cloud upload, device claiming and shared result management | Not implemented. Requires a supported integration and account/service workflow; local exports are the current alternative. |
| IPv6 test depth | Address reporting and an OS-routed IPv6 ping exist. No DHCPv6 exchange, RA analysis, per-router testing or complete interface-bound IPv6 validation. |
| Windows configuration parity | VLAN creation, static settings, MAC/speed changes, 802.1X and flash-port control still need Windows-specific backends and hardware validation. |
| Dedicated test-interface routing | DHCP now uses the selected capture interface. ICMP and HTTP still follow OS routing; TCP and direct DNS can bind a source. Multi-interface isolation needs further work. |
| G2-equivalent DNS presentation | PortStrider queries each DNS server; the G2 card uses repeated pings. A separate per-server ICMP display is not implemented. |
| Direct camera capture, localization and in-app updates | Attach existing image files; English UI and manual GitHub downloads. These convenience workflows remain absent. |
| Full reflector compatibility | Requires protocol-specific interoperability tests with physical NetAlly peers. |

## Hardware-dependent gaps

Loaded PoE/TruePower, electrical pair measurements, full wiremap and split-pair detection, remote cable IDs, and analog/digital tone generation require dedicated hardware. Ordinary host NICs cannot supply these measurements. Existing cable TDR and SFP diagnostics depend on driver support and do not replace a G2 cable-test port. Advertised PoE is decoded metadata, not a power measurement.

## Validation

Automated coverage includes INFORM with an existing address, DORA without an address or with APIPA, timeout and NAK outcomes, transaction/MAC/server matching, cancellation, Ethernet/IP/UDP framing and checksums, proxy challenge/response against a local server, profile export behavior and IPv6 descriptions. Release builds cover Linux and Windows x64/arm64. Physical-switch/DHCP interoperability and Windows execution still require field testing.
