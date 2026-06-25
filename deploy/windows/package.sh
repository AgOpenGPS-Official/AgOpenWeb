#!/usr/bin/env bash
# AgOpenWeb — build a deployable Windows bundle (all-in-one WebView launcher).
#
# Cross-publishes the self-contained Desktop exe on a build machine (dev Mac / Linux / CI)
# and zips it. On Windows the user just extracts and double-clicks AgOpenWeb.Desktop.exe:
# no args → a maximized window that starts the in-process guidance host and fills itself
# with the web UI in an embedded WebView2 (one double-click = host + UI on this box). The
# host still binds 0.0.0.0, so cab tablets/phones can connect over the LAN too. No installer,
# no service, no .NET install needed — it's app-like, the way the Windows AgOpen audience
# expects. (Linux/macOS use deploy/linux instead.)
#
# Requires the WebView2 Evergreen Runtime on the target PC for the embedded UI to render.
# It's pre-installed on Windows 11 and current Windows 10; the publish bundles the native
# WebView2Loader.dll (via Microsoft.Web.WebView2) but not the runtime itself.
#
# Usage:
#   ./package.sh                  # win-x64 bundle (default)
#   ./package.sh --arch arm64     # win-arm64 bundle (Windows on ARM)
#   ./package.sh --out /tmp       # choose output dir
#
# Requires: bash + the .NET 10 SDK. Runs anywhere dotnet can cross-publish.

set -euo pipefail

ARCH="x64"
OUT_DIR="$(pwd)"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --out) OUT_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

case "$ARCH" in
  x64|amd64)     RID="win-x64" ;;
  arm64|aarch64) RID="win-arm64" ;;
  *) echo "error: unsupported --arch '$ARCH' (use x64 or arm64)" >&2; exit 2 ;;
esac

command -v dotnet >/dev/null || { echo "error: dotnet SDK not found" >&2; exit 1; }
command -v zip >/dev/null || { echo "error: zip not found" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CSPROJ="$REPO_ROOT/Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj"

NAME="agopenweb-$RID"
STAGE="$(mktemp -d)/$NAME"
mkdir -p "$STAGE"

echo "==> Publishing self-contained $RID (no trim — the app uses reflection)…"
# DesktopOnly=true collapses the shared projects' net10.0;net10.0-ios multi-target to
# net10.0 so a cross-publish doesn't try to pull the iOS/Mono runtime packs (NU1102).
dotnet publish "$CSPROJ" \
  -c Release -r "$RID" --self-contained true \
  -p:DesktopOnly=true \
  -p:PublishTrimmed=false -p:PublishSingleFile=false \
  -o "$STAGE"

cp "$SCRIPT_DIR/README.md" "$STAGE/" 2>/dev/null || true

# Strip debug symbols — not needed at runtime. SkiaSharp ships an ~84 MB native
# libSkiaSharp.pdb that otherwise dominates the zip.
find "$STAGE" -name '*.pdb' -delete

mkdir -p "$OUT_DIR"
ZIP="$OUT_DIR/$NAME.zip"
echo "==> Writing ${ZIP} ..."
rm -f "$ZIP"
( cd "$(dirname "$STAGE")" && zip -r -q "$ZIP" "$NAME" )
rm -rf "$(dirname "$STAGE")"

echo "==> Done: $ZIP ($(du -h "$ZIP" | cut -f1))"
echo
echo "On Windows: extract the zip and run AgOpenWeb.Desktop.exe — the launcher opens."
