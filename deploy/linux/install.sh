#!/usr/bin/env bash
# AgOpenWeb — Linux systemd installer (Phase 10 appliance deployment).
#
# Publishes the self-contained headless host, installs it to /opt/agopenweb, creates
# a dedicated unprivileged service user, and installs + enables the systemd unit.
# The host then auto-starts on boot and serves the browser UI on :5174.
#
# Run on the target box (or cross-publish elsewhere and copy /opt/agopenweb + the
# unit over). Re-running is idempotent (upgrade in place).
#
#   sudo ./install.sh                 # publish for this box's arch (x64 by default)
#   sudo ./install.sh --arch arm64    # for a 64-bit ARM SBC (Raspberry Pi, etc.)
#   sudo ./install.sh --no-build      # skip publish; reinstall the unit from an
#                                     # already-published /opt/agopenweb
#
# Requires: bash, systemd, and (unless --no-build) the .NET 10 SDK to publish.

set -euo pipefail

ARCH="x64"
DO_BUILD=1
PREFIX="/opt/agopenweb"
STATE_DIR="/var/lib/agopenweb"
SVC_USER="agopenweb"
UNIT="agopenweb.service"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --no-build) DO_BUILD=0; shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ $EUID -ne 0 ]]; then
  echo "error: run as root (sudo)." >&2; exit 1
fi

case "$ARCH" in
  x64|amd64)  RID="linux-x64" ;;
  arm64|aarch64) RID="linux-arm64" ;;
  *) echo "error: unsupported --arch '$ARCH' (use x64 or arm64)" >&2; exit 2 ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CSPROJ="$REPO_ROOT/Platforms/AgValoniaGPS.Desktop/AgValoniaGPS.Desktop.csproj"

echo "==> AgOpenWeb installer (rid=$RID, prefix=$PREFIX, build=$DO_BUILD)"

if [[ $DO_BUILD -eq 1 ]]; then
  command -v dotnet >/dev/null || { echo "error: dotnet SDK not found" >&2; exit 1; }
  echo "==> Publishing self-contained build (no trim — the app uses reflection)…"
  # Self-contained: no .NET runtime needed on the appliance. NOT trimmed: the steer
  # wizard projects step ViewModels by reflection, which trimming would break.
  # DesktopOnly=true collapses the shared projects to the net10.0 TFM only. Without
  # it, a publish run from macOS also pulls their net10.0-ios TFM, which drags in the
  # Mono linux runtime pack for the RID and fails restore. On a Linux build box the
  # shared projects are already net10.0-only, so the flag is a harmless no-op there.
  dotnet publish "$CSPROJ" \
    -c Release -r "$RID" --self-contained true \
    -p:DesktopOnly=true \
    -p:PublishTrimmed=false -p:PublishSingleFile=false \
    -o /tmp/agopenweb-publish
  PUBLISH_SRC="/tmp/agopenweb-publish"
fi

echo "==> Creating service user '$SVC_USER' (if missing)…"
if ! id -u "$SVC_USER" >/dev/null 2>&1; then
  # System account, no login shell, home = state dir.
  useradd --system --no-create-home --home-dir "$STATE_DIR" --shell /usr/sbin/nologin "$SVC_USER"
fi
# Best-effort device groups (may not exist on every distro).
for g in dialout can; do getent group "$g" >/dev/null && usermod -aG "$g" "$SVC_USER" || true; done

if [[ $DO_BUILD -eq 1 ]]; then
  echo "==> Installing files to $PREFIX…"
  systemctl stop "$UNIT" 2>/dev/null || true
  mkdir -p "$PREFIX"
  rm -rf "$PREFIX.old"
  [[ -d "$PREFIX" && -n "$(ls -A "$PREFIX" 2>/dev/null)" ]] && cp -a "$PREFIX" "$PREFIX.old" || true
  rm -rf "${PREFIX:?}/"*
  cp -a "$PUBLISH_SRC/." "$PREFIX/"
  chmod +x "$PREFIX/AgValoniaGPS.Desktop"
fi

mkdir -p "$STATE_DIR"
chown -R "$SVC_USER":"$SVC_USER" "$PREFIX" "$STATE_DIR"

echo "==> Installing systemd unit…"
install -m 0644 "$SCRIPT_DIR/$UNIT" "/etc/systemd/system/$UNIT"
systemctl daemon-reload
systemctl enable "$UNIT"
systemctl restart "$UNIT"

echo "==> Done. Status:"
systemctl --no-pager --full status "$UNIT" || true
echo
echo "Logs:    journalctl -u $UNIT -f"
echo "Browser: http://<this-box-ip>:5174"
