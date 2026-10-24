#!/usr/bin/env bash
# Installs a netLX binary for the current user with a desktop entry and icon.
#   ./install-linux.sh [path/to/netLX]   (defaults to ./netLX next to this script)
# Set NETLX_PREFIX to install elsewhere (default: ~/.local).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="${1:-$HERE/netLX}"
PREFIX="${NETLX_PREFIX:-$HOME/.local}"

# Assets sit next to the script in release archives, or under packaging/linux in the source tree.
ASSETS="$HERE"
[[ -f "$ASSETS/netlx.desktop" ]] || ASSETS="$HERE/../packaging/linux"

if [[ ! -f "$SRC" ]]; then
  echo "netLX binary not found: $SRC" >&2
  exit 1
fi

install -Dm755 "$SRC" "$PREFIX/bin/netLX"
install -Dm644 "$ASSETS/netlx.png" "$PREFIX/share/icons/hicolor/256x256/apps/netlx.png"
mkdir -p "$PREFIX/share/applications"
sed "s|^Exec=.*|Exec=$PREFIX/bin/netLX|" "$ASSETS/netlx.desktop" > "$PREFIX/share/applications/netlx.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$PREFIX/share/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "$PREFIX/share/icons/hicolor" 2>/dev/null || true

echo "Installed $PREFIX/bin/netLX"
echo "Granting capture capabilities (sudo setcap)…"
if sudo setcap cap_net_raw,cap_net_admin,cap_net_bind_service+eip "$PREFIX/bin/netLX"; then
  echo "Done. Launch netLX from your application menu."
else
  echo "setcap failed; capture-based features stay unavailable until you run:" >&2
  echo "  sudo setcap cap_net_raw,cap_net_admin,cap_net_bind_service+eip $PREFIX/bin/netLX" >&2
fi
