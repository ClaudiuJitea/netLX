# netLX v1.4.0

The app has a new name, netLX, and joins the LX family alongside compositorLX and pdfLX. This release also adds a new icon, Linux desktop integration, more iPerf3 options, and import buttons for profiles and reports.

## Rebrand

- New name and icon in the window, the sidebar, the Windows executable and the Linux desktop entry. The sidebar footer now shows the app version.
- Existing profiles, sessions and exports move automatically to the new `netLX` data folders on first launch.
- The repository now lives at `github.com/ClaudiuJitea/netLX`. Old links redirect.

## New

- **iPerf3:** TCP or UDP; upload, download (`-R`) or bidirectional (`--bidir`) direction; 1–128 parallel streams; configurable UDP rate. Results show sent and received rates, TCP retransmits, and UDP jitter and loss. iperf3's own error messages are shown instead of a generic failure.
- **Import:** profiles (JSON) and report bundles (ZIP) can be imported from Profiles and Reports.
- **Linux install:** release archives include the binary, icon, desktop entry and `install-linux.sh`. The script installs to `~/.local` and grants capture capabilities.

## Fixed

- PDF reports stopped after about 48 lines without warning and left out target results. They now span multiple pages with page numbers, and include targets, job and site labels, addresses and technician notes.
- Fixed clipped iPerf3 port and rate fields in the Tools view.

## Downloads

`netLX-1.4.0-linux-x64.tar.gz`, `netLX-1.4.0-linux-arm64.tar.gz`, `netLX-linux-x64`, `netLX-linux-arm64`, `netLX-win-x64.exe`, `netLX-win-arm64.exe`.
Verify with `sha256sum -c SHA256SUMS --ignore-missing`. Binaries are no longer committed under `publish/`; GitHub Releases is the only place to download them.
