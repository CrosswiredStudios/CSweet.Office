#!/bin/sh
set -eu

force=false
if [ "$#" -gt 1 ]; then echo "usage: $0 [--force]" >&2; exit 2; fi
if [ "$#" -eq 1 ]; then
  [ "$1" = "--force" ] || { echo "usage: $0 [--force]" >&2; exit 2; }
  force=true
fi
if [ "$(id -u)" -ne 0 ]; then echo "Run this uninstaller with sudo." >&2; exit 1; fi

helper=$(dirname "$0")/csweet-office-storage.sh
[ -f "$helper" ] || helper=$(dirname "$0")/office-storage.sh
. "$helper"
office_storage_platform
if [ -e "$office_record" ] || [ -L "$office_record" ]; then
  office_load_storage || exit 2
else
  office_mount=/
  office_set_roots
fi
maintenance="$office_data_root"/maintenance
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

launchctl bootout system/com.csweet.office 2>/dev/null || true
launchctl bootout system/com.csweet.office.runtime 2>/dev/null || true
rm -f -- /Library/LaunchDaemons/com.csweet.office.node.plist
rm -f -- /Library/LaunchDaemons/com.csweet.office.runtime.plist
rm -rf -- /Library/Application\ Support/CSweet/Execution
rm -rf -- "$office_install_root"
[ "$office_data_root" = "$office_install_root" ] || rm -rf -- "$office_data_root"
rm -rf -- /Library/Application\ Support/CSweet/ArtifactMedia
rm -rf -- /Library/Application\ Support/CSweet/AgentRuntime
rm -rf -- /Library/Application\ Support/CSweet/InstallerPayload
rmdir /Library/Application\ Support/CSweet 2>/dev/null || true
rm -rf -- /var/run/csweet-av

if dscl . -read /Users/_csweetnode >/dev/null 2>&1; then dscl . -delete /Users/_csweetnode; fi
if dscl . -read /Groups/_csweet >/dev/null 2>&1; then dscl . -delete /Groups/_csweet; fi

rm -f -- /usr/local/sbin/csweet-configure-office
rm -f -- /usr/local/sbin/csweet-uninstall-office
pkgutil --forget com.csweet.office.payload >/dev/null 2>&1 || true

echo "C-Sweet RuntimeHost and Office were uninstalled. Revoke the node in fleet administration if it was not already revoked."

rm -f -- "$office_record"

rm -f -- /usr/local/sbin/csweet-office-start
