#!/usr/bin/env bash
# AgOpenWeb — build a deployable Linux bundle (Phase 10 appliance deployment).
#
# Cross-publishes the self-contained headless host on a build machine (e.g. the dev
# Mac) and bundles it with the systemd unit + install scripts into a single tarball
# you copy to a target board that has NO .NET SDK (e.g. the Uno Q). On the board:
#
#   tar xzf agopenweb-linux-arm64.tar.gz && cd agopenweb-linux-arm64
#   sudo ./install.sh --from app
#
# Usage:
#   ./package.sh                  # x86-64 bundle
#   ./package.sh --arch arm64     # 64-bit ARM bundle (Uno Q / Raspberry Pi / etc.)
#   ./package.sh --arch arm64 --out /tmp   # choose output dir
#
# Requires: bash + the .NET 10 SDK. Runs anywhere dotnet can cross-publish (macOS,
# Linux, Windows). Does NOT need to run on the target arch.

set -euo pipefail

ARCH="x64"
OUT_DIR="$(pwd)"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --out) OUT_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

case "$ARCH" in
  x64|amd64)  RID="linux-x64" ;;
  arm64|aarch64) RID="linux-arm64" ;;
  *) echo "error: unsupported --arch '$ARCH' (use x64 or arm64)" >&2; exit 2 ;;
esac

command -v dotnet >/dev/null || { echo "error: dotnet SDK not found" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CSPROJ="$REPO_ROOT/Platforms/AgValoniaGPS.Desktop/AgValoniaGPS.Desktop.csproj"

NAME="agopenweb-$RID"
STAGE="$(mktemp -d)/$NAME"
mkdir -p "$STAGE/app"

echo "==> Publishing self-contained $RID (no trim — the app uses reflection)…"
# See install.sh for why DesktopOnly=true + untrimmed + self-contained.
dotnet publish "$CSPROJ" \
  -c Release -r "$RID" --self-contained true \
  -p:DesktopOnly=true \
  -p:PublishTrimmed=false -p:PublishSingleFile=false \
  -o "$STAGE/app"

echo "==> Staging install scripts + unit…"
cp "$SCRIPT_DIR/install.sh" "$SCRIPT_DIR/uninstall.sh" \
   "$SCRIPT_DIR/agopenweb.service" "$SCRIPT_DIR/README.md" "$STAGE/"
chmod +x "$STAGE/install.sh" "$STAGE/uninstall.sh"

mkdir -p "$OUT_DIR"
TARBALL="$OUT_DIR/$NAME.tar.gz"
echo "==> Writing ${TARBALL} ..."
tar -C "$(dirname "$STAGE")" -czf "$TARBALL" "$NAME"
rm -rf "$(dirname "$STAGE")"

echo "==> Done: $TARBALL ($(du -h "$TARBALL" | cut -f1))"
echo
echo "On the target board (no .NET SDK needed):"
echo "  tar xzf $NAME.tar.gz && cd $NAME"
echo "  sudo ./install.sh --from app"
