#!/usr/bin/env bash
# Installs or removes netLX for the current user, with a menu entry and desktop shortcut
# that work on GNOME and KDE. Accepts the netLX-linux-x64/arm64 release binary or an AppImage.
# This file is self-contained: the icon is embedded in case icons/netLX.jpeg is not nearby.
#
#   ./netlx-setup.sh                         interactive menu
#   ./netlx-setup.sh install [file]          install (searches for netLX if omitted)
#   ./netlx-setup.sh [file]                  same as install
#   ./netlx-setup.sh uninstall               remove everything install created
#
# Installed files:
#   ~/.local/opt/netLX/                      netLX or netLX.AppImage (and the icon, if unconverted)
#   ~/.local/share/applications/netlx.desktop
#   ~/.local/share/icons/hicolor/*/apps/netlx.png
#   ~/Desktop/netlx.desktop
#
# Profiles, reports and captures (~/.local/share/netLX, ~/netLX) are never touched.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
OPT_DIR="$HOME/.local/opt/netLX"
TARGET=""  # set by do_install: the installed program
ICON_SRC_NAME="netLX.jpeg"
ICON_ROOT="$DATA_HOME/icons/hicolor"
ICON_SIZES=(16 24 32 48 64 128 256 512)
MENU_ENTRY="$DATA_HOME/applications/netlx.desktop"
CAPS="cap_net_raw,cap_net_admin,cap_net_bind_service+eip"

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

is_elf() {
  [[ "$(od -An -tx1 -N4 "$1" 2>/dev/null | tr -d ' \n')" == "7f454c46" ]]
}

# Newest netLX AppImage or release binary for this CPU next to this script, in the release
# folder, in the current folder, or in Downloads.
find_program() {
  local dir file newest="" arch
  case "$(uname -m)" in
    x86_64) arch=x64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) arch="$(uname -m)" ;;
  esac
  local dirs=("$HERE" "$HERE/../dist" "$PWD" "$HOME/Downloads" "$(xdg-user-dir DOWNLOAD 2>/dev/null || true)")
  shopt -s nullglob nocaseglob
  for dir in "${dirs[@]}"; do
    # [n] makes the second name a pattern; otherwise nocaseglob leaves it unmatched.
    for file in "$dir"/netlx*.appimage "$dir"/[n]etlx-linux-"$arch"; do
      if [[ -z "$newest" || "$file" -nt "$newest" ]]; then newest="$file"; fi
    done
  done
  shopt -u nullglob nocaseglob
  printf '%s\n' "$newest"
}

# Writes the icon to the given file: the project's JPEG if nearby, else the embedded copy.
extract_icon() {
  local candidate
  for candidate in "$HERE/../icons/$ICON_SRC_NAME" "$HERE/$ICON_SRC_NAME"; do
    if [[ -f "$candidate" ]]; then cp "$candidate" "$1"; return; fi
  done
  sed '1,/^__ICON_BASE64__$/d' "${BASH_SOURCE[0]}" | base64 -d > "$1"
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
Exec="$TARGET"
TryExec=$TARGET
Icon=$icon
Terminal=false
Categories=Network;System;Monitor;
Keywords=ethernet;network;lldp;cdp;dhcp;vlan;wifi;iperf;tester;
StartupWMClass=netLX
EOF
}

# Only entries that launch our copy are ours to delete; install-linux.sh uses the same name.
is_our_entry() {
  [[ -f "$1" ]] && grep -qF "Exec=\"$OPT_DIR/" "$1"
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
  [[ -n "$src" ]] || src="$(find_program)"
  [[ -n "$src" ]] || die "netLX not found. Pass its path: $0 install /path/to/netLX-linux-x64"
  [[ -f "$src" ]] || die "file not found: $src"
  is_elf "$src" || die "not a Linux program: $src (extract a .tar.gz first)"

  local name=netLX
  if is_appimage "$src"; then name=netLX.AppImage; fi
  TARGET="$OPT_DIR/$name"

  if [[ -e "$MENU_ENTRY" ]] && ! is_our_entry "$MENU_ENTRY"; then
    echo "Replacing the existing menu entry from another netLX install: $MENU_ENTRY"
  fi

  mkdir -p "$OPT_DIR"
  if [[ ! "$src" -ef "$TARGET" ]]; then
    # install(1) replaces the file, so this also works while netLX is running.
    install -m755 "$src" "$TARGET"
  fi
  chmod 755 "$TARGET"
  # Drop the other format left by an earlier install.
  if [[ "$name" == netLX ]]; then rm -f "$OPT_DIR/netLX.AppImage"; else rm -f "$OPT_DIR/netLX"; fi
  echo "Copied netLX to $TARGET"

  icon_src="$(mktemp)"
  extract_icon "$icon_src"
  icon="$(install_icon "$icon_src")"
  rm -f "$icon_src"
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
  if [[ "$name" == netLX.AppImage ]]; then
    echo "Note: capabilities set with setcap do not apply inside an AppImage, so packet capture,"
    echo "active DHCP and WiFi scans will be unavailable unless netLX is run with sudo."
  elif command -v sudo >/dev/null && command -v setcap >/dev/null &&
    echo "Granting capture capabilities (sudo setcap)..." &&
    sudo setcap "$CAPS" "$TARGET"; then
    echo "Capture capabilities granted. Restart netLX if it is running."
  else
    echo "Capture features stay unavailable until you run:" >&2
    echo "  sudo setcap $CAPS \"$TARGET\"" >&2
  fi
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
    if [[ -f "$file" ]] && our_install_present; then
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
    echo "netLX install not found; nothing to remove."
  fi
}

# The icon name is shared with install-linux.sh, so leave it alone if that install remains.
our_install_present() {
  [[ -d "$OPT_DIR" && ! -e "$HOME/.local/bin/netLX" ]]
}

menu() {
  echo "netLX setup"
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
  -h|--help) sed -n '2,/^set -euo/{/^#/s/^# \{0,1\}//p}' "${BASH_SOURCE[0]}" ;;
  *)
    if [[ -f "$1" ]]; then do_install "$1"; else
      die "unknown command or file: $1 (use install, uninstall or --help)"
    fi ;;
esac
exit 0

# Base64 copy of icons/netLX.jpeg at 512x512; nothing below this line is executed.
__ICON_BASE64__
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
AEKAAAAhSACkKUe12LL+VbKz/wDer6sj6dzT6G56ucz5DxJxpXVvQoXVtU04pTinGou7Ceqfckus
+YNjD/lWyt+ux+rI9ztjzBeZT2+0saw/jWtrW2cqbeiqwcWpQfca4eB9BrqjOUlh/GsKvMExe8wz
E6To3lpVdKrDurpXWmtGn1NGf9kqS8zfnL/jf4MDXt7y7ZZuyhY7QMtLsqjQi7ndXGdDok/0qb1T
7mvanH2TNy8zhnTueff4MBM5wPnnhpr0Hv8ALex/OmYbKF3ZYUqFtUW9Cd5VVFzXWov12nd0Oz8z
llS3zLnzs2IUo1rLC6SupU5rWM6jlu0010pPWWn6J2W13Nedsczbf0cMo4/a4Na1pUbena0a1NVF
F6OpJxXrm2m10JaaGUzvygeFzns+zNk2EKmPYbKlbTluRuaU1VpOXU5Lk+49DtNkmTMbzBj+HYph
tjC5w+wxCh56nKrCO6lKMn62T1freozBsKxHGc04LjmVs9WuIXNm6EXSq39Gak4Se7KG9JcWnuyX
SjDeScSxvLG0S1wGyxW8trf0Zp21zSo1HGFbdqqDcl06pEzmdwzJ5obIGY84Zjwq6y7YRure3s5U
pydeFPdl2RtLSTXRoYu2N4Xd4NtywjDcSpqleWtxVpVoKSluyVGeq1XB/Ie+80vmjH8AzVhFHBMZ
v8PoVLKU5wt6zhGUuytatLp04GPNiV9c4htqwO7vq9S4uq9erOrVqPWU5OjPi30skZ8k6m/5pNfy
s3/6rbfwzF5lLzSn5WL79VtvqGLTOnRYem2d5suMl5rs8YtlKdOm9y4pJ+20Zezj3+ld1Iyp5pLL
Nvf2uHZ8wJxrWV5TpwuqkOTTX4Kr8q9a+6omBT6d8zRK5x7IWMYJjllG7y/Sq9ioOrxjLe9dUpad
Sektehy0Mat29JcDZnb22yjZXeZxxmini+Jwj52oT4ScXxpU+5vezl3Euo+fcWxC5xXErrEMQrOt
eXVSVWrUf50m9X//ADqMu+alxDFK2dLXD7ul2HCra3jUslF+tq73Cc++mt3ToS7phZCmOsgKAZKA
AAAAAAAAAAAAAAAaAAAAACAAAAAGQrIUAgAKACAAQCgIAANSAAAUUEBBdSAFFQAIAIABQQCkYDKP
Z7GPyr5W/XY/Vkei80z+VW4/Urb6rPF7O8Ytsv55wTF7/snnSzuVVq9jjvS3dGuC6eZ222PM+H5v
zzWxbCOz+dJ21Gkuz09yW9FNPhqzHrHtfM453hh+JVMpYzKM8LxOTVuqvGMK0lo4P9GouHwtOsyT
b5TeTNlu0bCIKTs//FXFnJ9NGdCLSfdi04vvd0+S4SlCcZwlKM4tSjKL0aa5NPrPoavtxwvF9lt7
hONRvVmG4w+raTlTo60qlRxcVPe14a8G+HB6kqp35wmTgeZKxCjRzHj9jUklVuLSnVgnzkoTe9p8
k0zg5v2wbRMvZoxXCbi9tIStLidOKlZR4w11g+6nHR6mJst43f5cxu0xbCa3Yby2lvQk1qmuTjJd
MWtU0Zxvdo2zXPttbzz5gl1ZYnShuOvQjKXDqjUg95x7klwExvzHj/V4z6+d/YvTrsoHlMq31bEt
puD4heSi7i5xejXquK3U5SrJvRdHFmRsQxfYvhOHXccGwbEcUvqtGdOnOtGelOUotKSdRpJrXXXd
bMLWlerZ3Nvc0J7tehONWEuqUWmn4UWI7FZw81tq844G9OHnCfH51ni9gX5XsufGVP4UzKOJbStm
We8IspZ6sLujiFum9yNKo9yT03lCpTfGLa5PQxRlDMGBZc2u0sbtqdzTy9b3dadGEYOVSNJxlGC0
b114rmyRnlknU7fzSj3trF9ov6rbfUMX6M+icfz/ALIMwYnPEMZwK+vL2cYwlWqWkk2orRLhUS4I
4MczbDdfXZWuv/iz/wD2iJnTIhhbLeC3uYsdssIwyG/d3dRU4a8o9cn3EtW+4jO22vG6Gz/JWEZC
yzWnRrSpxq3FaD0mqalrvN9Eqk05d5d08vsfznkvJV/jGK39K/qYhcValO0p0bbejRt97VJScuEp
cE+pJd0xpmzHrvM+Y7/GcQlrcXdRzcU+EI8owXcSSXyF1kfQ+I04bbdj9K8oxh6aMLb1iuDdaMfX
Q+DUjo13dOo+Y5RcZOMk4yT0aa0afUz3uxjPnpEzS7i77LPCbuHYrunTW9LhxhOK6XF/sbOHtXxP
L2OZvuMWyr55hb3i7LcUa9Dse5W/OceL1UufcevWIjKch40agFUABRQQEAAAUEKAIAAKQFFBAQUE
RQAICgAAAAAAMAAAAAAFIUEEAKUQFIAAAAFIBSAAACgQAAAAAAAAAAAAAAAAF0AEAAAAoEAAAAoE
KCAAAAAKBAAABQBAAAAAAAAAAAAAAAAAAAAKAABAABQABBACoohQABCkAAugAEAAAFAhQQACk0AA
pAAKAIUAgEKAICgoAACAoAAEIAAKBSACkYAAAoAhSAACgQpAAAAAAAAAAAAFZBoAKNSaDQKoAADU
mg0CBdSAChEAF1BC6ABqQAUakADUAAAAAAAFBAAGoAF1BBoALqQANSkAFGpABdQQAUEAFAIBQTQA
UgAAAAXUgAAagAUEAAAAAAAAAAAAAAB//9k=
