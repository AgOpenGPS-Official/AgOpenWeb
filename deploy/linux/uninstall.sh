#!/usr/bin/env bash
# AgOpenWeb — Linux systemd uninstaller. Stops + disables the service and removes
# the unit and /opt/agopenweb. Keeps /home/agopenweb (field data + config) unless
# --purge is given.
#
#   sudo ./uninstall.sh            # remove the service + program files
#   sudo ./uninstall.sh --purge    # also delete /home/agopenweb and the user

set -euo pipefail
PURGE=0
[[ "${1:-}" == "--purge" ]] && PURGE=1
[[ $EUID -ne 0 ]] && { echo "run as root (sudo)." >&2; exit 1; }

UNIT="agopenweb.service"
systemctl stop "$UNIT" 2>/dev/null || true
systemctl disable "$UNIT" 2>/dev/null || true
rm -f "/etc/systemd/system/$UNIT"
systemctl daemon-reload
rm -rf /opt/agopenweb /opt/agopenweb.old

if [[ $PURGE -eq 1 ]]; then
  rm -rf /home/agopenweb
  id -u agopenweb >/dev/null 2>&1 && userdel agopenweb || true
  echo "purged program, data, and user."
else
  echo "removed program + service. Field data kept in /home/agopenweb."
fi
