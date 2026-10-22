# PortStrider v1.3.0

This release adds direct DNS diagnostics and editable AutoTest targets, and improves the reliability of existing network tests. It retains v1.2's DHCP INFORM fix for adapters that already have an IPv4 address.

- **DNS tool:** A, AAAA, CNAME, NS, MX, TXT and PTR queries with TTLs, response status and transport. Validate response source/ID/question, reject empty or negative answers as a pass, and retry truncated UDP replies over TCP. AutoTest displays DNS and ICMP timings independently.
- **Target editor:** add, copy, remove, enable and edit ping, TCP and HTTP targets in Profiles, with endpoint/port validation.
- **Selected-source probes:** bind Linux ICMP through iputils-ping, support IPv4/IPv6 TCP sources, and bind HTTP connections. Report unsupported Windows ICMP source enforcement as a warning.
- **TLS:** remove the URL-based certificate-validation bypass; show certificate errors and final URLs.
- **VLAN/capture:** exact, unambiguous capture-device selection; top-nine VLANs plus Other VLANs and Untagged accounting based on captured bytes. Priority tag 0 remains distinct.
- **Temporary configuration:** preserve existing VLANs/addresses/routes, register rollback for partial changes, restore original forced link mode, validate masks and stop AutoTest if required configuration cannot be fully applied.
- **Tools lifecycle:** Stop tests, cancellation on navigation/adapter changes, and bounded concurrent port checks. Profile exports now omit both proxy and 802.1X passwords.

Read the [implementation audit and LinkRunner G2 comparison](https://github.com/ClaudiuJitea/PortStrider/blob/v1.3.0/docs/linkrunner-g2-comparison.md) for the complete feature inventory, evidence, remaining software gaps and hardware limitations.

## Downloads

Four self-contained binaries: `PortStrider-linux-x64`, `PortStrider-linux-arm64`, `PortStrider-win-x64.exe`, `PortStrider-win-arm64.exe`. Verify with `sha256sum -c SHA256SUMS-supported` after downloading all listed assets, or check the matching entry for a single binary. Source-tree binaries are also updated under `publish/`.

Linux: reapply network capabilities to the replacement binary; install iputils-ping for source-bound ICMP. Windows: Npcap is required for raw DHCP/capture. Existing temporary-interface/address/route conflicts now stop the test instead of replacing host configuration.

## Validation and limitations

135 automated tests pass (19 Core, 116 Infrastructure); Release UI build completes with zero warnings/errors. Headless Avalonia checks the new DNS and target screens and exercises target Copy/Remove bindings. All four platform binaries are compiled; release assets have SHA-256 checksums.

Local protocol fixtures and command-runner simulations do not replace physical switch/DHCP/802.1X/VLAN testing. Windows and arm64 execution remain unverified. Source binding does not provide complete interface isolation: hostname lookup, traceroute and iPerf still use host routing. Dedicated loaded PoE/wiremap/tone, Link-Live integration and deeper IPv6 diagnostics remain unavailable. The audit also records pre-existing PDF length/Unicode and ZIP attachment-import limitations.
