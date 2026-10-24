# netLX v1.2.0

DHCP tests now work alongside an existing IPv4 configuration: AutoTest sends DHCP INFORM and waits for a server acknowledgement without replacing the OS lease. Unconfigured adapters still run full Discover/Offer/Request/Ack. A separate libpcap/Npcap handle avoids UDP/68 conflicts and runs alongside switch discovery.

- Add authenticated proxy settings for AutoTest HTTP/TLS targets; omit proxy passwords from exported profiles.
- Show IPv6 link-local, unique-local and global addresses in AutoTest, and preserve link-local addresses in saved reports.
- Refresh adapter state before DHCP; distinguish configuration ACKs from acquired leases.
- Match DHCP replies to transaction, MAC, selected server and offered address; preserve cancellation.
- Document the [LinkRunner G2 feature comparison](https://github.com/ClaudiuJitea/netLX/blob/v1.2.0/docs/linkrunner-g2-comparison.md), remaining software gaps and hardware limits.

Downloads: `netLX-linux-x64`, `netLX-linux-arm64`, `netLX-win-x64.exe`, `netLX-win-arm64.exe`. All four are built from v1.2.0. Verify downloaded assets with `sha256sum -c SHA256SUMS-supported` from the same release.

Linux: grant network capabilities to the new binary after replacing the previous version. Windows: install Npcap for DHCP and capture. INFORM timeouts are warnings; they do not prove the current lease is invalid. INFORM does not test lease acquisition.

Validation: 86 automated tests pass; Release UI build has no warnings or errors. Headless UI rendering checks the updated profile screen. Physical DHCP/switch interoperability and execution on Windows/arm64 remain unverified in this environment.
