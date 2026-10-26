#!/usr/bin/env bash
# Convertr_svc installer for Ubuntu with systemd (tested on Ubuntu 24.04 LTS; see "Built and tested on" in the README).
# Copyright (C) 2026 JeepNWilly. Free software under the GNU GPL v3 or later; no warranty.
#
# Usage (as root, from the unpacked tarball):
#   sudo ./scripts/install.sh --media-root /mnt/nas/media [--media-root /other/root] [--listen 0.0.0.0] [--port 8484]
#                             [--scratch /srv/convertr-scratch] [--no-start] [--dry-run]
# Re-running upgrades the binary and the unit; /etc/convertr-svc/appsettings.json and /var/lib/convertr-svc are never overwritten.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PREFIX=/opt/convertr-svc
DATA=/var/lib/convertr-svc
CONF=/etc/convertr-svc
UNIT=/etc/systemd/system/convertr-svc.service
DROPIN_DIR=/etc/systemd/system/convertr-svc.service.d
LISTEN=127.0.0.1
PORT=8484
SCRATCH=""
ROOTS=()
START=1
DRY=0

die() { echo "install.sh: $*" >&2; exit 1; }
run() { if [ "$DRY" = 1 ]; then echo "[dry-run] $*"; else "$@"; fi; }

while [ $# -gt 0 ]; do
  case "$1" in
    --media-root) ROOTS+=("${2:?--media-root needs a folder}"); shift 2 ;;
    --listen)     LISTEN="${2:?}"; shift 2 ;;
    --port)       PORT="${2:?}"; shift 2 ;;
    --scratch)    SCRATCH="${2:?}"; shift 2 ;;
    --no-start)   START=0; shift ;;
    --dry-run)    DRY=1; shift ;;
    -h|--help)    sed -n '2,10p' "$0"; exit 0 ;;
    *) die "unknown option $1" ;;
  esac
done

[ "$DRY" = 1 ] || [ "$(id -u)" = 0 ] || die "run as root (sudo)"
[ -x "$HERE/convertr-svc" ] || die "convertr-svc binary not found next to scripts/ ($HERE)"
[ -f "$HERE/deploy/convertr-svc.service" ] || die "deploy/convertr-svc.service missing"
[ "$DRY" = 1 ] || command -v systemctl >/dev/null || die "systemd is required"
[ "${#ROOTS[@]}" -gt 0 ] || echo "note: no --media-root given; edit AllowedRoots in $CONF/appsettings.json afterwards (the web UI cannot browse anything until then)"
for r in "${ROOTS[@]:-}"; do [ -z "$r" ] || [ -d "$r" ] || echo "warning: $r does not exist yet (mount it before starting, or it will be ignored)"; done

echo "== user and folders"
if ! getent passwd convertr >/dev/null; then
  run useradd --system --home-dir "$DATA" --shell /usr/sbin/nologin --user-group convertr
fi
# GPU access (the groups exist on Ubuntu; ignore if one is missing)
for g in render video; do getent group "$g" >/dev/null && run usermod -aG "$g" convertr || true; done
run install -d -m 0755 -o root -g root "$PREFIX" "$PREFIX/docs"
run install -d -m 0750 -o convertr -g convertr "$DATA"
run install -d -m 0755 -o root -g convertr "$CONF"
if [ -n "$SCRATCH" ]; then
  run install -d -m 0700 -o convertr -g convertr "$SCRATCH"
  run install -m 0600 -o convertr -g convertr /dev/null "$SCRATCH/.convertr-scratch"
fi

echo "== binary"
run install -m 0755 -o root -g root "$HERE/convertr-svc" "$PREFIX/convertr-svc.new"
run mv -f "$PREFIX/convertr-svc.new" "$PREFIX/convertr-svc"
[ -d "$HERE/docs" ] && run cp -r "$HERE/docs/." "$PREFIX/docs/" || true

echo "== configuration"
if [ -f "$CONF/appsettings.json" ]; then
  echo "keeping existing $CONF/appsettings.json"
else
  json_roots=""
  for r in "${ROOTS[@]:-}"; do [ -z "$r" ] || json_roots+="${json_roots:+, }\"$r\""; done
  if [ "$DRY" = 1 ]; then echo "[dry-run] write $CONF/appsettings.json (Listen=$LISTEN Port=$PORT AllowedRoots=[$json_roots])"; else
    cat > "$CONF/appsettings.json" <<JSON
{
  "Convertr": {
    "Listen": "$LISTEN",
    "Port": $PORT,
    "AllowedRoots": [ $json_roots ],
    "DataDir": "$DATA"
  }
}
JSON
    chown root:convertr "$CONF/appsettings.json"; chmod 0640 "$CONF/appsettings.json"
  fi
fi

echo "== systemd unit"
run install -m 0644 "$HERE/deploy/convertr-svc.service" "$UNIT"
# writable paths for later milestones (S3+: the service moves/writes files in the media roots and uses the scratch folder)
# RequiresMountsFor= is a [Unit] setting (in [Service] systemd ignores it with "Unknown key name"); ReadWritePaths= is a [Service] setting.
render_paths_conf() {   # $1 = the space separated folders
  printf '[Unit]\nRequiresMountsFor=%s\n\n[Service]\nReadWritePaths=%s\n' "$1" "$1"
}
# an older installer (0.3.0 to 0.3.2) wrote RequiresMountsFor= under [Service]; true when the file at $1 has that mistake
paths_conf_is_old() {
  [ -f "$1" ] && awk '/^\[/{sec=$0} /^RequiresMountsFor=/ && sec=="[Service]"{bad=1} END{exit !bad}' "$1"
}
rw=""
for r in "${ROOTS[@]:-}"; do [ -z "$r" ] || rw+=" $r"; done
[ -n "$SCRATCH" ] && rw+=" $SCRATCH"
if [ -n "$rw" ]; then
  run install -d -m 0755 "$DROPIN_DIR"
  if [ "$DRY" = 1 ]; then echo "[dry-run] write $DROPIN_DIR/paths.conf (RequiresMountsFor and ReadWritePaths=$rw)"; else
    render_paths_conf "${rw# }" > "$DROPIN_DIR/paths.conf"
  fi
elif paths_conf_is_old "$DROPIN_DIR/paths.conf"; then
  # upgrade without --media-root: keep the folders of the existing file, put RequiresMountsFor= where systemd reads it
  old_rw="$(sed -n 's/^ReadWritePaths=//p' "$DROPIN_DIR/paths.conf" | head -1)"
  if [ "$DRY" = 1 ]; then echo "[dry-run] repair $DROPIN_DIR/paths.conf (move RequiresMountsFor= to [Unit], folders: $old_rw)"; else
    render_paths_conf "$old_rw" > "$DROPIN_DIR/paths.conf"
  fi
fi
run systemctl daemon-reload
run systemctl enable convertr-svc.service

if [ "$START" = 1 ]; then
  run systemctl restart convertr-svc.service
  if [ "$DRY" = 0 ]; then
    sleep 4
    systemctl --no-pager --lines=0 status convertr-svc.service || true
    echo
    echo "== first-run setup (only while no password exists)"
    journalctl -u convertr-svc.service -n 50 --no-pager 2>/dev/null | grep -E "FIRST-RUN SETUP" | tail -1 || echo "(no setup token in the log: a password already exists)"
    echo
    echo "Open http://$([ "$LISTEN" = 0.0.0.0 ] && hostname -I | awk '{print $1}' || echo "$LISTEN"):$PORT/"
    echo "Lost the password?  sudo -u convertr $PREFIX/convertr-svc --reset-password"
  fi
else
  echo "Not started (--no-start). Start with: sudo systemctl start convertr-svc"
fi
