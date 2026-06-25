#!/usr/bin/env bash
# AgOpenWeb — Linux desktop launcher installer (per-user, no sudo).
#
# Copies the extracted bundle into ~/.local/share/agopenweb and registers an application-menu
# entry that runs the all-in-one launcher (host + embedded WebView UI). Re-running upgrades
# in place. The embedded WebView needs a system WebKit + libsoup (see README "Requirements");
# this installer does NOT touch system packages — install those with your package manager.
#
#   ./install.sh              # install to ~/.local/share/agopenweb + menu entry
#   ./install.sh --uninstall  # remove the menu entry + installed copy
#   ./install.sh --prefix DIR # install under DIR instead of ~/.local/share/agopenweb

set -euo pipefail

PREFIX="$HOME/.local/share/agopenweb"
APPS_DIR="$HOME/.local/share/applications"
DESKTOP_ID="AgOpenWeb.desktop"
ACTION="install"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall) ACTION="uninstall"; shift ;;
    --prefix) PREFIX="$2"; shift 2 ;;
    -h|--help) sed -n '2,17p' "$0"; exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "$ACTION" == "uninstall" ]]; then
  echo "==> Removing menu entry + installed copy…"
  rm -f "$APPS_DIR/$DESKTOP_ID"
  rm -rf "$PREFIX"
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS_DIR" 2>/dev/null || true
  echo "==> Done. (User data under ~/Documents/AgOpenWeb was left untouched.)"
  exit 0
fi

[[ -f "$SCRIPT_DIR/app/AgOpenWeb.Desktop" ]] \
  || { echo "error: run this from the extracted bundle (no app/AgOpenWeb.Desktop next to install.sh)" >&2; exit 1; }

echo "==> Installing to $PREFIX …"
mkdir -p "$PREFIX" "$APPS_DIR"
# Fresh copy of the program files (leave nothing stale from a prior version).
rm -rf "$PREFIX/app"
cp -a "$SCRIPT_DIR/app" "$PREFIX/app"
cp -a "$SCRIPT_DIR/run.sh" "$PREFIX/run.sh"
chmod +x "$PREFIX/run.sh" "$PREFIX/app/AgOpenWeb.Desktop"
[[ -f "$SCRIPT_DIR/AgOpenWeb.png" ]] && cp -a "$SCRIPT_DIR/AgOpenWeb.png" "$PREFIX/AgOpenWeb.png" || true

echo "==> Registering application-menu entry…"
ICON="$PREFIX/AgOpenWeb.png"
[[ -f "$ICON" ]] || ICON="utilities-terminal"   # fallback if the png is missing
sed -e "s|__EXEC__|$PREFIX/run.sh|g" -e "s|__ICON__|$ICON|g" \
    "$SCRIPT_DIR/AgOpenWeb.desktop" > "$APPS_DIR/$DESKTOP_ID"
chmod +x "$APPS_DIR/$DESKTOP_ID"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS_DIR" 2>/dev/null || true

echo "==> Done. Launch 'AgOpenWeb' from your application menu, or run: $PREFIX/run.sh"
echo "    If the window is blank, install the WebKit runtime — see README \"Requirements\"."
