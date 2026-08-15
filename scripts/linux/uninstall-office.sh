#!/bin/sh
set -eu

force=false
if [ "$#" -gt 1 ]; then echo "usage: $0 [--force]" >&2; exit 2; fi
if [ "$#" -eq 1 ]; then
  [ "$1" = "--force" ] || { echo "usage: $0 [--force]" >&2; exit 2; }
  force=true
fi
if [ "$(id -u)" -ne 0 ]; then echo "Run this uninstaller as root." >&2; exit 1; fi

maintenance=/var/lib/csweet/office/maintenance
drain_state=
[ ! -f "$maintenance/drain-state" ] || drain_state=$(tr -d '\r\n' < "$maintenance/drain-state")
active_count=0
if [ -d "$maintenance/active-assignments" ]; then
  active_count=$(find "$maintenance/active-assignments" -type f -name '*.active' | wc -l | tr -d ' ')
fi
if [ "$force" != true ] && { [ "$drain_state" != draining ] || [ "$active_count" -ne 0 ]; }; then
  echo "Drain this node in C-Sweet and wait for active assignments to reach zero before uninstalling." >&2
  echo "Use --force only after the node is revoked and active workloads may be terminated." >&2
  exit 3
fi

systemctl disable --now csweet-office-node.service 2>/dev/null || true
systemctl disable --now csweet-office-runtime.service 2>/dev/null || true
rm -f -- /etc/systemd/system/csweet-office-node.service
rm -f -- /etc/systemd/system/csweet-office-runtime.service
rm -f -- /etc/csweet/office.env
rm -f -- /etc/csweet/runtime-host.env
systemctl daemon-reload

rm -rf -- /opt/csweet/office
rm -rf -- /var/lib/csweet/office
rm -rf -- /var/lib/csweet/office
rm -rf -- /var/lib/csweet/artifact-media
rmdir /opt/csweet 2>/dev/null || true
rmdir /etc/csweet 2>/dev/null || true
rmdir /var/lib/csweet 2>/dev/null || true

if getent passwd csweet-node >/dev/null 2>&1; then userdel csweet-node; fi
if getent passwd csweet-vm >/dev/null 2>&1; then userdel csweet-vm; fi
if getent group csweet-runtime >/dev/null 2>&1; then groupdel csweet-runtime; fi

echo "C-Sweet RuntimeHost and Office were uninstalled. Revoke the node in fleet administration if it was not already revoked."
