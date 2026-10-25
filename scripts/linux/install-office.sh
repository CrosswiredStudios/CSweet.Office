#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then echo "Run this installer as root." >&2; exit 1; fi
if [ "$#" -lt 2 ]; then echo "usage: $0 PACKAGE_ROOT https://control-plane [--enrollment-token-file PATH] [--result-job-id ID] [--security-profile baseline|hardened|development] [--dedicated-host] [--allow-development-assignments]" >&2; exit 2; fi
package_root=$(readlink -f "$1")
control_plane=$2
shift 2
token_file=
result_job_id=
result_file=
security_profile=baseline
mixed_use=true
allow_development=false
storage_volume=
while [ "$#" -gt 0 ]; do
  case "$1" in
    --storage-volume) [ "$#" -ge 2 ] || exit 2; storage_volume=$2; shift 2 ;;
    --enrollment-token-file) [ "$#" -ge 2 ] || exit 2; token_file=$2; shift 2 ;;
    --result-job-id) [ "$#" -ge 2 ] || exit 2; result_job_id=$2; shift 2 ;;
    --security-profile) [ "$#" -ge 2 ] || exit 2; security_profile=$2; shift 2 ;;
    --dedicated-host) mixed_use=false; shift ;;
    --allow-development-assignments) allow_development=true; shift ;;
    *) echo "Unknown installer option: $1" >&2; exit 2 ;;
  esac
done
case "$security_profile" in baseline|hardened) ;; development) [ "$allow_development" = true ] || { echo "Development posture requires --allow-development-assignments." >&2; exit 2; }; echo "WARNING: development posture is for disposable test data and credentials only." >&2 ;; *) echo "Invalid security profile." >&2; exit 2;; esac
if [ ! -r /etc/os-release ]; then echo "Ubuntu 24.04 LTS is required." >&2; exit 2; fi
. /etc/os-release
if [ "${ID:-}" != ubuntu ] || [ "${VERSION_ID:-}" != 24.04 ]; then
  echo "This release supports Ubuntu 24.04 LTS only." >&2
  exit 2
fi
case "$(uname -m)" in x86_64|aarch64|arm64) ;; *) echo "Only x86-64 and arm64 hosts are supported." >&2; exit 2;; esac
package_owner=$(stat -c '%u:%g' "$package_root")
[ "$package_owner" = 0:0 ] || { echo "The Office package must be owned by root." >&2; exit 2; }
if find "$package_root" -perm /022 -print -quit | grep -q .; then
  echo "The Office package may not contain group/world-writable paths." >&2
  exit 2
fi
if [ -n "$result_job_id" ]; then
  case "$result_job_id" in *[!0-9a-f]*|'') echo "The installer result job ID is invalid." >&2; exit 2;; esac
  [ "${#result_job_id}" -eq 32 ] || { echo "The installer result job ID is invalid." >&2; exit 2; }
  install -d -o root -g root -m 0755 /var/lib/csweet/setup
  result_file=/var/lib/csweet/setup/local-provisioning-$result_job_id.result
fi
write_result() {
  if [ -n "$result_file" ]; then printf '%s\n' "$1" > "$result_file"; chmod 0644 "$result_file"; fi
}
trap 'write_result failed' 0
case "$control_plane" in https://*) ;; *) echo "Control-plane URL must use HTTPS." >&2; exit 2;; esac
if [ ! -x "$package_root/CSweet.Office.RuntimeHost" ] || [ ! -x "$package_root/CSweet.Office.Node" ] ||
   [ ! -x "$package_root/CSweet.Office.Runtime.Firecracker.Helper" ] ||
   [ ! -x "$package_root/firecracker/firecracker" ] || [ ! -x "$package_root/firecracker/jailer" ] ||
   [ ! -f "$package_root/firecracker/vmlinux" ] || [ ! -f "$package_root/firecracker/initrd.img" ] ||
   [ ! -f "$package_root/runtime-manifest.json" ]; then
  echo "The signed RuntimeHost/Office/Firecracker package is incomplete." >&2; exit 2
fi
if [ ! -r /sys/fs/cgroup/cgroup.controllers ] || [ ! -r /dev/kvm ] || [ ! -w /dev/kvm ]; then
  echo "Firecracker requires cgroup v2 and read/write access to /dev/kvm." >&2; exit 2
fi
if find "$package_root" -type l -print -quit | grep -q .; then
  echo "Execution packages may not contain symbolic links." >&2; exit 2
fi
. "$package_root/office-storage.sh"
office_select_storage "$storage_volume" || exit 2
# Older installations have no registration yet and must retain their system destination.
if [ ! -e "$office_record" ] && [ -e /opt/csweet/office/CSweet.Office.Node ] && [ "$office_mount" != / ]; then
  echo 'Keep the system volume for this existing Office. Moving an installed Office is not supported.' >&2
  exit 2
fi
if [ -f /etc/systemd/system/csweet-office-node.service ] || [ -e "$office_install_root"/CSweet.Office.Node ]; then
  maintenance="$office_data_root"/maintenance
  drain_state=
  [ ! -f "$maintenance/drain-state" ] || drain_state=$(tr -d '\r\n' < "$maintenance/drain-state")
  active_count=0
  if [ -d "$maintenance/active-assignments" ]; then
    active_count=$(find "$maintenance/active-assignments" -type f -name '*.active' | wc -l | tr -d ' ')
  fi
  if [ "$drain_state" != draining ] || [ "$active_count" -ne 0 ]; then
    echo "Drain this node in C-Sweet and wait for active assignments to reach zero before upgrading." >&2
    exit 3
  fi
  systemctl stop csweet-office-node.service 2>/dev/null || true
  systemctl stop csweet-office-runtime.service 2>/dev/null || true
else
  # Clean v1 cutover: discard the legacy daemon, state, certificate, and enrollment.
  systemctl disable --now csweet-execution-node.service csweet-runtime-host.service 2>/dev/null || true
  rm -f -- /etc/systemd/system/csweet-execution-node.service /etc/systemd/system/csweet-runtime-host.service
  rm -rf -- /opt/csweet/execution-node /opt/csweet/runtime-host /var/lib/csweet/execution-node /var/lib/csweet/agent-runtime
  systemctl daemon-reload
fi
if [ -n "$token_file" ]; then
  [ -f "$token_file" ] && [ ! -L "$token_file" ] || { echo "The protected enrollment input is invalid." >&2; exit 2; }
  token=$(tr -d '\r\n' < "$token_file")
  rm -f -- "$token_file"
elif [ -t 0 ]; then printf 'Enrollment token: ' >&2; stty -echo; IFS= read -r token; stty echo; printf '\n' >&2
else IFS= read -r token; fi
if [ "${#token}" -lt 32 ] || [ "${#token}" -gt 256 ]; then echo "Invalid enrollment token." >&2; exit 2; fi

office_save_storage
install -d -o root -m 0755 /usr/sbin
install -m 0644 "$package_root/office-storage.sh" /usr/sbin/csweet-office-storage.sh
install -m 0755 "$package_root/office-start.sh" /usr/sbin/csweet-office-start
install -d -m 0755 "$office_install_root" /etc/csweet "$office_data_root" "$office_data_root" "$office_data_root"/firecracker
chown root:root "$office_data_root"/firecracker
chmod 0700 "$office_data_root"/firecracker
cp -R "$package_root/." "$office_install_root"/
chown -R root:root "$office_install_root"
chmod 0755 "$office_install_root"/CSweet.Office.RuntimeHost "$office_install_root"/CSweet.Office.Node "$office_install_root"/CSweet.Office.Runtime.Firecracker.Helper
chmod 0755 "$office_install_root"/firecracker/firecracker "$office_install_root"/firecracker/jailer
chmod 0644 "$office_install_root"/firecracker/vmlinux
chmod 0644 "$office_install_root"/firecracker/initrd.img
chmod 0644 "$office_install_root"/runtime-manifest.json
id csweet-node >/dev/null 2>&1 || useradd --system --home "$office_data_root" --shell /usr/sbin/nologin csweet-node
id csweet-vm >/dev/null 2>&1 || useradd --system --home /nonexistent --shell /usr/sbin/nologin csweet-vm
getent group csweet-runtime >/dev/null 2>&1 || groupadd --system csweet-runtime
usermod -a -G csweet-runtime csweet-node
install -d -o csweet-node -g csweet-node -m 0700 "$office_data_root"/node
install -d -o root -g root -m 0700 "$office_data_root"/authorization
vm_uid=$(id -u csweet-vm)
vm_gid=$(id -g csweet-vm)
install -d -o csweet-node -g csweet-runtime -m 0770 "$office_artifact_root"
if [ ! -f "$office_data_root"/runtime-host.key ]; then
  umask 007
  dd if=/dev/urandom bs=32 count=1 2>/dev/null | base64 > "$office_data_root"/runtime-host.key
fi
chown root:csweet-runtime "$office_data_root"/runtime-host.key
chmod 0640 "$office_data_root"/runtime-host.key
"$office_install_root"/CSweet.Office.Node \
  --initialize-headquarters-assignment-trust "$control_plane" \
  "$office_data_root"/authorization/headquarters-trust.json
chown root:root "$office_data_root"/authorization/headquarters-trust.json
chmod 0600 "$office_data_root"/authorization/headquarters-trust.json
install -o csweet-node -g csweet-node -m 0600 /dev/null "$office_data_root"/node/enrollment.secret
printf '%s' "$token" > "$office_data_root"/node/enrollment.secret
unset token
cat > /etc/csweet/office.env <<EOF
CSweet__Office__Node__ControlPlaneUrl=$control_plane
CSweet__Office__Node__StateDirectory="$office_data_root"/node
CSweet__Office__Node__ArtifactCacheDirectory="$office_data_root"/node/artifact-cache
CSweet__Office__Node__ArtifactMediaDirectory="$office_artifact_root"
CSweet__Office__Node__EnrollmentTokenFilePath="$office_data_root"/node/enrollment.secret
CSweet__Office__Node__SecurityProfile=$security_profile
CSweet__Office__Node__MixedUseHost=$mixed_use
CSweet__Office__Node__AllowDevelopmentAssignments=$allow_development
CSweet__Office__RuntimeHost__UnixSocketPath=/run/csweet/csweet-office-runtime-v1.sock
CSweet__Office__RuntimeHost__Authentication__SharedKeyFilePath="$office_data_root"/runtime-host.key
CSweet__Office__RuntimeHost__Authorization__StateDirectory="$office_data_root"/authorization
EOF
chmod 0600 /etc/csweet/office.env
cat > /etc/csweet/runtime-host.env <<EOF
CSWEET_FIRECRACKER_DATA_ROOT="$office_data_root"/firecracker
CSWEET_FIRECRACKER_PACKAGE_ROOT="$office_install_root"/firecracker
CSWEET_FIRECRACKER_WORKLOAD_UID=$vm_uid
CSWEET_FIRECRACKER_WORKLOAD_GID=$vm_gid
CSWEET_FIRECRACKER_GUEST_VSOCK_PORT=5000
CSWEET_ARTIFACT_MEDIA_ROOT="$office_artifact_root"
EOF
chmod 0600 /etc/csweet/runtime-host.env
for service in csweet-office-runtime csweet-office-node; do
  office_render_unit "$package_root/$service.service" "/etc/systemd/system/$service.service"
  chmod 0644 "/etc/systemd/system/$service.service"
done
systemctl daemon-reload
systemctl enable --now csweet-office-runtime.service csweet-office-node.service
write_result completed
trap - 0
