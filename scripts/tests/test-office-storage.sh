#!/bin/sh
set -eu
script_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_root/../office-storage.sh"

# Real selection/registration logic, deterministic inventory, no service or system changes.
test_root=$(mktemp -d)
trap 'rm -f "$test_root/registration" "$test_root/runtime.service" "$test_root/node.plist"; rmdir "$test_root"' EXIT
office_storage_platform() { office_platform=linux; office_record=$test_root/registration; }
office_assert_path() { office_safe_value "$1"; }
office_list_volumes() { printf '%s\n' "$inventory"; }
expect() { [ "$1" = "$2" ] || { echo "Expected '$2', got '$1'" >&2; exit 1; }; }
reject() { if ( "$@" ) >/dev/null 2>&1; then echo "Expected rejection: $*" >&2; exit 1; fi; }
system='root-uuid|/|System|ext4|536870912000|214748364800|true|-'
alternate='data-uuid|/mnt/Office Drive|Data|xfs|1073741824000|858993459200|false|-'
inventory=$(printf '%s\n%s' "$system" "$alternate")
office_select_storage '' </dev/null
expect "$office_volume_id" root-uuid
office_select_storage data-uuid </dev/null
expect "$office_install_root" '/mnt/Office Drive/CSweet/Office/program'
expect "$office_data_root" '/mnt/Office Drive/CSweet/Office/data'
reject office_select_storage missing
inventory=$alternate
office_select_storage '' </dev/null
expect "$office_volume_id" data-uuid
inventory=$(printf '%s\n%s' "$alternate" 'third|/mnt/Other|Other|ext4|1073741824000|858993459200|false|-')
reject office_select_storage ''
inventory='full|/|System|ext4|536870912000|1024|true|-'
reject office_select_storage ''
inventory='usb|/mnt/USB|USB|ext4|536870912000|214748364800|false|Removable'
reject office_select_storage usb
inventory=$alternate
office_select_storage data-uuid </dev/null
office_save_storage
office_load_storage
expect "$office_mount" '/mnt/Office Drive'
reject office_select_storage root-uuid
inventory='replacement|/mnt/Office Drive|Data|xfs|1073741824000|858993459200|false|-'
reject office_load_storage
office_mount='/mnt/Office & Data'
office_set_roots
office_render_unit "$script_root/../linux/csweet-office-runtime.service" "$test_root/runtime.service"
grep -F 'ExecStart="/mnt/Office & Data/CSweet/Office/program/CSweet.Office.RuntimeHost"' "$test_root/runtime.service" >/dev/null
grep -F 'ReadWritePaths="/mnt/Office & Data/CSweet/Office/data"' "$test_root/runtime.service" >/dev/null
grep -F 'RequiresMountsFor="/mnt/Office & Data/CSweet/Office/program"' "$test_root/runtime.service" >/dev/null
control_plane='https://headquarters.example/?a=1&b=2'
security_profile=baseline; mixed_use=true; allow_development=false
office_render_plist "$script_root/../macos/com.csweet.office.node.plist" "$test_root/node.plist"
if command -v python >/dev/null 2>&1 && python -c 'import plistlib' >/dev/null 2>&1; then
  python_command=python
else
  python_command=python3
fi
"$python_command" - "$test_root/node.plist" <<'PY'
import plistlib, sys
with open(sys.argv[1], 'rb') as stream:
    plist = plistlib.load(stream)
assert plist['ProgramArguments'] == ['/usr/local/sbin/csweet-office-start', 'node']
env = plist['EnvironmentVariables']
assert env['CSweet__Office__Node__StateDirectory'] == '/mnt/Office & Data/CSweet/Office/data/node'
assert env['CSweet__Office__Node__ControlPlaneUrl'] == 'https://headquarters.example/?a=1&b=2'
PY
office_volume_id=data-uuid
findmnt() { printf '/dev/mapped-data[/subvolume]\n'; }
readlink() { printf '/dev/real-data\n'; }
office_verify_linux_mount
readlink() { case "$2" in /dev/disk/by-uuid/*) printf '/dev/expected\n' ;; *) printf '/dev/replacement\n' ;; esac; }
reject office_verify_linux_mount
echo 'Unix Office storage: selection/registration checks and service/plist rendering passed.'
