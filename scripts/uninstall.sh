#!/usr/bin/env bash
# Convertr_svc uninstaller (DRAFT). Keeps /var/lib/convertr-svc (settings, queue, history) and /etc/convertr-svc unless --purge.
# It never touches media folders, scratch folders or originals.
# Copyright (C) 2026 JeepNWilly. Free software under the GNU GPL v3 or later; no warranty.
set -euo pipefail
PURGE=0; [ "${1:-}" = "--purge" ] && PURGE=1
[ "$(id -u)" = 0 ] || { echo "run as root" >&2; exit 1; }
systemctl disable --now convertr-svc.service 2>/dev/null || true
rm -f /etc/systemd/system/convertr-svc.service
rm -rf /etc/systemd/system/convertr-svc.service.d /opt/convertr-svc
systemctl daemon-reload
if [ "$PURGE" = 1 ]; then
  rm -rf /var/lib/convertr-svc /etc/convertr-svc
  userdel convertr 2>/dev/null || true
  echo "Removed program, settings and data."
else
  echo "Removed the program. Kept /var/lib/convertr-svc and /etc/convertr-svc (use --purge to delete them)."
fi
