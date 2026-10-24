#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BIN="$ROOT/NetLX.UI/bin/Debug/net8.0/netLX"
if [[ ! -x "$BIN" ]]; then
  echo "Build the UI project first: dotnet build NetLX.UI"
  exit 1
fi
sudo setcap cap_net_raw,cap_net_admin,cap_net_bind_service+eip "$BIN"
getcap "$BIN"
echo "You can now capture without running the UI as root: $BIN"
