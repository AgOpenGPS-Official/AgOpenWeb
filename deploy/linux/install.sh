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
#   sudo ./install.sh --arch arm64    # publish for a 64-bit ARM SBC (Uno Q, Pi, …)
#   sudo ./install.sh --from ./app    # install a PRE-PUBLISHED bundle (no .NET SDK
#                                     # on the box) — the package.sh tarball flow
#   sudo ./install.sh --no-build      # reinstall the unit only; files already in place
#
# Appliance flow (no SDK on the target, e.g. the Uno Q): on a build machine run
#   deploy/linux/package.sh --arch arm64
# copy the resulting agopenweb-linux-arm64.tar.gz to the board, then:
#   tar xzf agopenweb-linux-arm64.tar.gz && cd agopenweb-linux-arm64
#   sudo ./install.sh --from app
#
# Requires: bash + systemd always; the .NET 10 SDK only in the default (build) mode.

set -euo pipefail

ARCH="x64"
MODE="build"          # build | from | unit-only
FROM_DIR=""
PREFIX="/opt/agopenweb"
STATE_DIR="/home/agopenweb"   # the agopenweb user's home — where people look for user data
SVC_USER="agopenweb"
UNIT="agopenweb.service"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --from) MODE="from"; FROM_DIR="$2"; shift 2 ;;   # install pre-published files (no SDK)
    --no-build) MODE="unit-only"; shift ;;           # reinstall unit only; files already in place
    -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
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

# Auto-detect a bundle: if no mode was forced and a published app/ sits next to this
# script (the package.sh tarball layout), install from it instead of trying to build.
# Lets a bare `sudo ./install.sh` Just Work on a target with no .NET SDK.
if [[ "$MODE" == "build" && -f "$SCRIPT_DIR/app/AgValoniaGPS.Desktop" ]]; then
  MODE="from"; FROM_DIR="$SCRIPT_DIR/app"
  echo "==> Detected published bundle in ./app — installing from it (no build)."
fi

echo "==> AgOpenWeb installer (mode=$MODE, rid=$RID, prefix=$PREFIX)"

PUBLISH_SRC=""
if [[ "$MODE" == "build" ]]; then
  command -v dotnet >/dev/null || { echo "error: dotnet SDK not found (use --from <dir> to install a pre-published bundle)" >&2; exit 1; }
  REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
  CSPROJ="$REPO_ROOT/Platforms/AgValoniaGPS.Desktop/AgValoniaGPS.Desktop.csproj"
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
elif [[ "$MODE" == "from" ]]; then
  [[ -d "$FROM_DIR" && -f "$FROM_DIR/AgValoniaGPS.Desktop" ]] \
    || { echo "error: --from '$FROM_DIR' is not a published app dir (no AgValoniaGPS.Desktop)" >&2; exit 1; }
  PUBLISH_SRC="$(cd "$FROM_DIR" && pwd)"
fi

echo "==> Creating service user '$SVC_USER' (if missing)…"
if ! id -u "$SVC_USER" >/dev/null 2>&1; then
  # System account, no login shell, home in /home so it sits with the other users.
  useradd --system --create-home --home-dir "$STATE_DIR" --shell /usr/sbin/nologin "$SVC_USER"
else
  # Existing account (possibly from an earlier /var/lib home) — repoint its home.
  usermod --home "$STATE_DIR" "$SVC_USER" 2>/dev/null || true
fi
# Best-effort device groups (may not exist on every distro).
for g in dialout can; do getent group "$g" >/dev/null && usermod -aG "$g" "$SVC_USER" || true; done

# SkiaSharp native deps. The host composites boundary imagery with SkiaSharp, whose
# bundled libSkiaSharp.so dynamically links libfontconfig + libGL. They are NOT part
# of the self-contained .NET bundle, so a minimal board (e.g. the Uno Q) lacks
# libfontconfig and the imagery-capture child fails ("type initializer for
# SkiaSharp.SKImageInfo threw"). The host stays up (capture is crash-isolated) but the
# background is blank. Best-effort install on apt systems; skipped elsewhere with a hint.
if command -v apt-get >/dev/null 2>&1; then
  echo "==> Ensuring SkiaSharp native deps (libfontconfig1, libgl1)…"
  apt-get install -y --no-install-recommends libfontconfig1 libgl1 \
    || echo "   (auto-install failed — if boundary imagery is blank, run: sudo apt-get install libfontconfig1 libgl1)"
else
  echo "==> NOTE: install libfontconfig + libGL via your package manager for boundary imagery."
fi

if [[ -n "$PUBLISH_SRC" ]]; then
  echo "==> Installing files to $PREFIX (from $PUBLISH_SRC)…"
  systemctl stop "$UNIT" 2>/dev/null || true
  mkdir -p "$PREFIX" "$STATE_DIR"
  # One-time recovery from earlier data locations: pre-v26.5.110 builds could store data
  # INSIDE the program dir (/opt/agopenweb/AgValoniaGPS); v26.5.110–112 used /var/lib/
  # agopenweb. If the /home data root has none yet, bring the first one found across (the
  # /opt one must be rescued before /opt is wiped below).
  if [[ ! -d "$STATE_DIR/AgValoniaGPS" ]]; then
    for old in "$PREFIX/AgValoniaGPS" /var/lib/agopenweb/AgValoniaGPS; do
      if [[ -d "$old" ]]; then
        echo "==> Recovering data from $old → $STATE_DIR/AgValoniaGPS"
        cp -a "$old" "$STATE_DIR/"
        break
      fi
    done
  fi
  rm -rf "$PREFIX.old"
  [[ -n "$(ls -A "$PREFIX" 2>/dev/null)" ]] && cp -a "$PREFIX" "$PREFIX.old" || true
  rm -rf "${PREFIX:?}/"*
  cp -a "$PUBLISH_SRC/." "$PREFIX/"
  chmod +x "$PREFIX/AgValoniaGPS.Desktop"
fi

mkdir -p "$STATE_DIR"
chown -R "$SVC_USER":"$SVC_USER" "$PREFIX" "$STATE_DIR"
# World-readable, daemon-write-only data home: any login user can browse/back up the
# fields, only the agopenweb service can modify them. The unit's StateDirectoryMode +
# UMask keep NEW files this way; this opens up any pre-existing 0700/0600 data (and the
# data just migrated in from /opt). a+rX = read for all + traverse on dirs, NO write.
chmod 0755 "$STATE_DIR"
[[ -d "$STATE_DIR/AgValoniaGPS" ]] && chmod -R a+rX "$STATE_DIR/AgValoniaGPS"

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
