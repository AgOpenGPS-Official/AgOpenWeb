#!/usr/bin/env bash
# Packages the macOS arm64 all-in-one launcher as a .dmg containing AgOpenWeb.app.
# The .app double-clicks straight into the WebView launcher (Program.cs defaults macOS
# → launcher; daemons pass --headless). Self-contained — no .NET install needed on the
# target. WKWebView (the embedded UI) is a system framework, so nothing extra to bundle.
#
# Usage:
#   ./package.sh                 # → ./dist/agopenweb-macos-arm64.dmg
#   ./package.sh --out /tmp/dist
#
# NOTE: this ad-hoc-signs for LOCAL launch. Distribution needs Developer ID signing +
# notarization (a separate step; the team has the account) or Gatekeeper blocks it on
# other Macs.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT_DIR="$REPO_ROOT/dist"
RID="osx-arm64"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) OUT_DIR="$2"; shift 2 ;;
    *) echo "error: unknown arg '$1'" >&2; exit 2 ;;
  esac
done

VERSION="$(grep -E '#define VERSION "' "$REPO_ROOT/sys/version.h" | sed -E 's/.*"([0-9.]+)".*/\1/')"
PROJ="$REPO_ROOT/Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj"
STAGE="$(mktemp -d)"
APP="$STAGE/AgOpenWeb.app"
trap 'rm -rf "$STAGE"' EXIT

echo "[macos] publishing self-contained $RID launcher (v$VERSION)…"
dotnet publish "$PROJ" -c Release -r "$RID" --self-contained true -p:DesktopOnly=true \
  -o "$APP/Contents/MacOS" >/dev/null

echo "[macos] assembling AgOpenWeb.app…"
mkdir -p "$APP/Contents/Resources"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>AgOpenWeb</string>
  <key>CFBundleDisplayName</key><string>AgOpenWeb</string>
  <key>CFBundleIdentifier</key><string>com.agopenweb.desktop</string>
  <key>CFBundleExecutable</key><string>AgOpenWeb.Desktop</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <!-- WKWebView (the launcher's embedded UI) loads the in-process http://localhost host. -->
  <key>NSAppTransportSecurity</key><dict><key>NSAllowsLocalNetworking</key><true/></dict>
</dict>
</plist>
PLIST

chmod +x "$APP/Contents/MacOS/AgOpenWeb.Desktop"

# Ad-hoc sign so Gatekeeper lets it launch locally (proper Developer ID + notarization
# is the distribution step). --deep covers the bundled dylibs.
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || echo "[macos] codesign skipped"

echo "[macos] building .dmg…"
mkdir -p "$OUT_DIR"
DMG="$OUT_DIR/agopenweb-macos-arm64.dmg"
STAGE_DMG="$(mktemp -d)"
cp -R "$APP" "$STAGE_DMG/"
ln -s /Applications "$STAGE_DMG/Applications"
rm -f "$DMG"
hdiutil create -volname "AgOpenWeb" -srcfolder "$STAGE_DMG" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE_DMG"

echo "[macos] ✅ $DMG ($(du -h "$DMG" | cut -f1))"
