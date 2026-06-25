#!/usr/bin/env bash
# AgOpenWeb — build a deployable Linux DESKTOP launcher bundle.
#
# The desktop twin of the Windows/macOS launcher: a single maximized window that starts
# the in-process guidance host and fills itself with the web UI in an embedded WebView
# (Avalonia's WebKitGTK/WPE backend). This is the Linux *desktop* app — distinct from the
# headless appliance daemon in deploy/linux (systemd). The host still binds 0.0.0.0, so cab
# tablets/phones can connect over the LAN too.
#
# Cross-publishes the self-contained Desktop exe on a build machine (dev Mac / Linux / CI)
# and tars it with a run wrapper, a .desktop entry, and an installer. On the target Linux
# desktop the user extracts and runs ./run.sh (or ./install.sh to add a menu entry).
#
# The embedded WebView needs a system WebKit + libsoup (see launcher README); they are NOT
# in the self-contained .NET bundle — pulling GUI runtime deps is normal for a desktop app.
#
# Usage:
#   ./package.sh                  # linux-x64 bundle (default)
#   ./package.sh --arch arm64     # linux-arm64 bundle (ARM desktop / SBC with a display)
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
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

case "$ARCH" in
  x64|amd64)     RID="linux-x64" ;;
  arm64|aarch64) RID="linux-arm64" ;;
  *) echo "error: unsupported --arch '$ARCH' (use x64 or arm64)" >&2; exit 2 ;;
esac

command -v dotnet >/dev/null || { echo "error: dotnet SDK not found" >&2; exit 1; }
command -v tar >/dev/null || { echo "error: tar not found" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
CSPROJ="$REPO_ROOT/Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj"

NAME="agopenweb-launcher-$RID"
STAGE="$(mktemp -d)/$NAME"
mkdir -p "$STAGE/app"

echo "==> Publishing self-contained $RID (no trim — the app uses reflection)…"
# DesktopOnly=true collapses the shared projects' net10.0;net10.0-ios multi-target to
# net10.0 so a cross-publish doesn't pull the iOS/Mono runtime packs (NU1102).
dotnet publish "$CSPROJ" \
  -c Release -r "$RID" --self-contained true \
  -p:DesktopOnly=true \
  -p:PublishTrimmed=false -p:PublishSingleFile=false \
  -o "$STAGE/app"

echo "==> Staging launcher run wrapper, .desktop entry, installer + README…"
cp "$SCRIPT_DIR/run.sh" "$SCRIPT_DIR/install.sh" \
   "$SCRIPT_DIR/AgOpenWeb.desktop" "$SCRIPT_DIR/README.md" "$STAGE/"
chmod +x "$STAGE/run.sh" "$STAGE/install.sh"
# App icon for the menu entry (cosmetic).
cp "$REPO_ROOT/Platforms/AgOpenWeb.Desktop/Assets/Images/TractorAoG.png" "$STAGE/AgOpenWeb.png" 2>/dev/null || true

# Strip debug symbols — not needed at runtime. SkiaSharp ships an ~84 MB native
# libSkiaSharp.so.pdb that would otherwise dominate the tarball.
find "$STAGE/app" -name '*.pdb' -delete

mkdir -p "$OUT_DIR"
TARBALL="$OUT_DIR/$NAME.tar.gz"
echo "==> Writing ${TARBALL} ..."
# --no-xattrs: drop macOS com.apple.provenance xattrs so GNU tar on the target stays quiet.
tar --no-xattrs -C "$(dirname "$STAGE")" -czf "$TARBALL" "$NAME"
rm -rf "$(dirname "$STAGE")"

echo "==> Done: $TARBALL ($(du -h "$TARBALL" | cut -f1))"
echo
echo "On the Linux desktop:"
echo "  tar xzf $NAME.tar.gz && cd $NAME"
echo "  ./run.sh            # run in place"
echo "  ./install.sh        # add an application-menu entry (per-user, no sudo)"
