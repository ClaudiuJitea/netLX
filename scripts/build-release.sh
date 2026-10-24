#!/usr/bin/env bash
# Builds self-contained single-file netLX binaries and release archives into dist/.
#   ./scripts/build-release.sh            all supported RIDs
#   ./scripts/build-release.sh linux-x64  selected RIDs only
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
DIST="$ROOT/dist"
RIDS=("$@")
[[ ${#RIDS[@]} -gt 0 ]] || RIDS=(linux-x64 linux-arm64 win-x64 win-arm64)

rm -rf "$DIST"
mkdir -p "$DIST"
for rid in "${RIDS[@]}"; do
  out="$ROOT/publish/$rid"
  rm -rf "$out"
  dotnet publish "$ROOT/NetLX.UI" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$out"

  case "$rid" in
    win-*)
      cp "$out/netLX.exe" "$DIST/netLX-$rid.exe" ;;
    linux-*)
      cp "$out/netLX" "$DIST/netLX-$rid"
      stage="$(mktemp -d)/netLX-$VERSION-$rid"
      mkdir -p "$stage"
      cp "$out/netLX" "$ROOT/packaging/linux/netlx.desktop" "$ROOT/packaging/linux/netlx.png" \
         "$ROOT/scripts/install-linux.sh" "$stage/"
      tar -C "$(dirname "$stage")" -czf "$DIST/netLX-$VERSION-$rid.tar.gz" "$(basename "$stage")"
      rm -rf "$(dirname "$stage")" ;;
  esac
done

(cd "$DIST" && sha256sum -- * > SHA256SUMS)
echo "netLX $VERSION release assets:"
ls -la "$DIST"
