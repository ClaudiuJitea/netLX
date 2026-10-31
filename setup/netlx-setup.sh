#!/usr/bin/env bash
# Installs or removes the netLX AppImage for the current user, with a menu entry and
# desktop shortcut that work on GNOME and KDE. No root is needed.
#
#   ./netlx-setup.sh                         interactive menu
#   ./netlx-setup.sh install [file.AppImage] install (searches for the AppImage if omitted)
#   ./netlx-setup.sh uninstall               remove everything install created
#
# Installed files:
#   ~/.local/opt/netLX/                      netLX.AppImage (and the icon, if unconverted)
#   ~/.local/share/applications/netlx.desktop
#   ~/.local/share/icons/hicolor/*/apps/netlx.png
#   ~/Desktop/netlx.desktop
#
# Profiles, reports and captures (~/.local/share/netLX, ~/netLX) are never touched.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
OPT_DIR="$HOME/.local/opt/netLX"
APPIMAGE="$OPT_DIR/netLX.AppImage"
ICON_SRC_NAME="netLX.jpeg"
ICON_ROOT="$DATA_HOME/icons/hicolor"
ICON_SIZES=(16 24 32 48 64 128 256 512)
MENU_ENTRY="$DATA_HOME/applications/netlx.desktop"

die() { echo "Error: $*" >&2; exit 1; }

desktop_dir() {
  local dir=""
  command -v xdg-user-dir >/dev/null && dir="$(xdg-user-dir DESKTOP 2>/dev/null || true)"
  # xdg-user-dir answers $HOME when no desktop folder is configured.
  if [[ -z "$dir" || "$dir" == "$HOME" ]]; then dir="$HOME/Desktop"; fi
  printf '%s\n' "$dir"
}

# True when the file is a type 1 or 2 AppImage: an ELF with the "AI" magic at offset 8.
is_appimage() {
  [[ "$(od -An -tx1 -N4 "$1" 2>/dev/null | tr -d ' \n')" == "7f454c46" ]] &&
    [[ "$(od -An -tx1 -j8 -N3 "$1" 2>/dev/null | tr -d ' \n')" =~ ^4149(01|02)$ ]]
}

# Newest netLX*.AppImage next to this script, in the release folder, or in Downloads.
find_appimage() {
  local dir file newest=""
  local dirs=("$HERE" "$HERE/../dist" "$PWD" "$HOME/Downloads" "$(xdg-user-dir DOWNLOAD 2>/dev/null || true)")
  shopt -s nullglob nocaseglob
  for dir in "${dirs[@]}"; do
    for file in "$dir"/netlx*.appimage; do
      if [[ -z "$newest" || "$file" -nt "$newest" ]]; then newest="$file"; fi
    done
  done
  shopt -u nullglob nocaseglob
  printf '%s\n' "$newest"
}

find_icon() {
  local candidate
  for candidate in "$HERE/../icons/$ICON_SRC_NAME" "$HERE/$ICON_SRC_NAME"; do
    if [[ -f "$candidate" ]]; then printf '%s\n' "$candidate"; return; fi
  done
}

# resize_png <source> <size> <dest>; returns non-zero if no image tool is available.
resize_png() {
  local src="$1" size="$2" dest="$3"
  if command -v magick >/dev/null; then
    magick "$src" -resize "${size}x${size}" "$dest"
  elif command -v convert >/dev/null; then
    convert "$src" -resize "${size}x${size}" "$dest"
  elif command -v ffmpeg >/dev/null; then
    ffmpeg -loglevel error -y -i "$src" -vf "scale=$size:$size" "$dest"
  elif command -v python3 >/dev/null && python3 -c 'import PIL' 2>/dev/null; then
    python3 -c 'import sys; from PIL import Image
Image.open(sys.argv[1]).convert("RGB").resize((int(sys.argv[2]),) * 2, Image.LANCZOS).save(sys.argv[3])' \
      "$src" "$size" "$dest"
  else
    return 1
  fi
}

# Icon themes only load PNG/SVG, so the JPEG is converted. Without an image tool the JPEG
# is kept and referenced by absolute path, which GNOME and KDE both accept. Prints the
# value for the desktop entry's Icon= key.
install_icon() {
  local src="$1" size dest
  for size in "${ICON_SIZES[@]}"; do
    dest="$ICON_ROOT/${size}x${size}/apps/netlx.png"
    mkdir -p "$(dirname "$dest")"
    if ! resize_png "$src" "$size" "$dest"; then
      rm -f "$ICON_ROOT"/*/apps/netlx.png
      cp "$src" "$OPT_DIR/netlx.jpeg"
      printf '%s\n' "$OPT_DIR/netlx.jpeg"
      return
    fi
  done
  printf 'netlx\n'
}

write_entry() {
  local dest="$1" icon="$2"
  mkdir -p "$(dirname "$dest")"
  cat > "$dest" <<EOF
[Desktop Entry]
Type=Application
Name=netLX
GenericName=Network Field Tester
Comment=Ethernet and WiFi diagnostics: AutoTest, switch discovery, capture and cable checks
Exec="$APPIMAGE"
TryExec=$APPIMAGE
Icon=$icon
Terminal=false
Categories=Network;System;Monitor;
Keywords=ethernet;network;lldp;cdp;dhcp;vlan;wifi;iperf;tester;
StartupWMClass=netLX
EOF
}

# Only entries that launch our AppImage are ours to delete; install-linux.sh uses the same name.
is_our_entry() {
  [[ -f "$1" ]] && grep -qF "Exec=\"$APPIMAGE\"" "$1"
}

refresh_desktop() {
  # A theme cache is only refreshed if one exists, so that none is left behind on uninstall.
  if [[ -f "$ICON_ROOT/icon-theme.cache" ]] && command -v gtk-update-icon-cache >/dev/null; then
    gtk-update-icon-cache -f -t -q "$ICON_ROOT" 2>/dev/null || true
  fi
  if command -v update-desktop-database >/dev/null; then
    update-desktop-database -q "$(dirname "$MENU_ENTRY")" 2>/dev/null || true
  fi
  # KDE keeps its own menu cache.
  local tool
  for tool in kbuildsycoca6 kbuildsycoca5; do
    if command -v "$tool" >/dev/null; then "$tool" >/dev/null 2>&1 || true; fi
  done
}

do_install() {
  local src="${1:-}" icon_src icon desktop shortcut
  [[ -n "$src" ]] || src="$(find_appimage)"
  [[ -n "$src" ]] || die "no netLX AppImage found. Pass its path: $0 install /path/to/netLX.AppImage"
  [[ -f "$src" ]] || die "file not found: $src"
  is_appimage "$src" || die "not an AppImage: $src (use scripts/install-linux.sh for the plain binary)"

  icon_src="$(find_icon)"
  [[ -n "$icon_src" ]] || die "icon $ICON_SRC_NAME not found in $HERE/../icons or $HERE"

  if [[ -e "$MENU_ENTRY" ]] && ! is_our_entry "$MENU_ENTRY"; then
    echo "Replacing the existing menu entry from another netLX install: $MENU_ENTRY"
  fi

  mkdir -p "$OPT_DIR"
  if [[ ! "$src" -ef "$APPIMAGE" ]]; then
    # install(1) replaces the file, so this also works while netLX is running.
    install -m755 "$src" "$APPIMAGE"
  fi
  chmod 755 "$APPIMAGE"
  echo "Copied AppImage to $APPIMAGE"

  icon="$(install_icon "$icon_src")"
  [[ "$icon" == "netlx" ]] || echo "No image tool found (ImageMagick, ffmpeg or Pillow); using the JPEG icon as is."

  write_entry "$MENU_ENTRY" "$icon"
  echo "Added application menu entry $MENU_ENTRY"

  desktop="$(desktop_dir)"
  if [[ -d "$desktop" ]]; then
    shortcut="$desktop/netlx.desktop"
    write_entry "$shortcut" "$icon"
    chmod 755 "$shortcut"
    # GNOME will not launch a desktop file until it is marked trusted; KDE needs the exec bit.
    if command -v gio >/dev/null; then
      gio set "$shortcut" metadata::trusted true 2>/dev/null || true
    fi
    echo "Added desktop shortcut $shortcut"
  fi

  refresh_desktop
  echo
  echo "netLX is installed. Launch it from your application menu."
  echo "Note: capabilities set with setcap do not apply inside an AppImage, so packet capture,"
  echo "active DHCP and WiFi scans will be unavailable unless netLX is run with sudo."
}

do_uninstall() {
  local shortcut removed=0 file
  shortcut="$(desktop_dir)/netlx.desktop"

  for file in "$MENU_ENTRY" "$shortcut"; do
    if is_our_entry "$file"; then
      rm -f "$file"
      echo "Removed $file"
      removed=1
    fi
  done

  for file in "$ICON_ROOT"/*/apps/netlx.png; do
    if [[ -f "$file" ]] && is_appimage_install_present; then
      rm -f "$file"
      removed=1
    fi
  done

  if [[ -d "$OPT_DIR" ]]; then
    rm -rf "$OPT_DIR"
    echo "Removed $OPT_DIR"
    removed=1
  fi
  rmdir "$HOME/.local/opt" 2>/dev/null || true

  refresh_desktop
  if [[ "$removed" -eq 1 ]]; then
    echo
    echo "netLX is uninstalled. Your profiles and reports were kept in $DATA_HOME/netLX and ~/netLX."
  else
    echo "netLX AppImage install not found; nothing to remove."
  fi
}

# The icon name is shared with install-linux.sh, so leave it alone if that install remains.
is_appimage_install_present() {
  [[ -d "$OPT_DIR" && ! -e "$HOME/.local/bin/netLX" ]]
}

menu() {
  echo "netLX AppImage setup"
  echo "  1) Install"
  echo "  2) Uninstall"
  echo "  3) Quit"
  local choice
  read -r -p "Choose an option [1-3]: " choice
  case "$choice" in
    1) do_install ;;
    2) do_uninstall ;;
    3) ;;
    *) die "invalid choice: $choice" ;;
  esac
}

[[ "$(id -u)" -ne 0 ]] || die "run this as your normal user, not root"

case "${1:-}" in
  "") menu ;;
  install) do_install "${2:-}" ;;
  uninstall) do_uninstall ;;
  -h|--help) sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' ;;
  *) die "unknown command: $1 (use install, uninstall or --help)" ;;
esac
