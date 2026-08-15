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
while [ "$#" -gt 0 ]; do
  case "$1" in
    --enrollment-token-file) [ "$#" -ge 2 ] || exit 2; token_file=$2; shift 2 ;;
    --result-job-id) [ "$#" -ge 2 ] || exit 2; result_job_id=$2; shift 2 ;;
    --security-profile) [ "$#" -ge 2 ] || exit 2; security_profile=$2; shift 2 ;;
    --dedicated-host) mixed_use=false; shift ;;
    --allow-development-assignments) allow_development=true; shift ;;
    *) echo "Unknown installer option: $1" >&2; exit 2 ;;
  esac
done
case "$security_profile" in baseline|hardened) ;; development) [ "$allow_development" = true ] || { echo "Development posture requires --allow-development-assignments." >&2; exit 2; }; echo "WARNING: development posture is for disposable test data and credentials only." >&2 ;; *) echo "Invalid security profile." >&2; exit 2;; esac
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
if [ -f /etc/systemd/system/csweet-office-node.service ] || [ -e /opt/csweet/office/CSweet.Office.Node ]; then
  maintenance=/var/lib/csweet/office/maintenance
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

install -d -m 0755 /opt/csweet/office /etc/csweet /var/lib/csweet/office /var/lib/csweet/office /var/lib/csweet/office/firecracker
chown root:root /var/lib/csweet/office/firecracker
chmod 0700 /var/lib/csweet/office/firecracker
cp -R "$package_root/." /opt/csweet/office/
chown -R root:root /opt/csweet/office
chmod 0755 /opt/csweet/office/CSweet.Office.RuntimeHost /opt/csweet/office/CSweet.Office.Node /opt/csweet/office/CSweet.Office.Runtime.Firecracker.Helper
chmod 0755 /opt/csweet/office/firecracker/firecracker /opt/csweet/office/firecracker/jailer
chmod 0644 /opt/csweet/office/firecracker/vmlinux
chmod 0644 /opt/csweet/office/firecracker/initrd.img
chmod 0644 /opt/csweet/office/runtime-manifest.json
id csweet-node >/dev/null 2>&1 || useradd --system --home /var/lib/csweet/office --shell /usr/sbin/nologin csweet-node
id csweet-vm >/dev/null 2>&1 || useradd --system --home /nonexistent --shell /usr/sbin/nologin csweet-vm
getent group csweet-runtime >/dev/null 2>&1 || groupadd --system csweet-runtime
usermod -a -G csweet-runtime csweet-node
install -d -o csweet-node -g csweet-node -m 0700 /var/lib/csweet/office/node
install -d -o root -g root -m 0700 /var/lib/csweet/office/authorization
vm_uid=$(id -u csweet-vm)
vm_gid=$(id -g csweet-vm)
install -d -o csweet-node -g csweet-runtime -m 0770 /var/lib/csweet/artifact-media
if [ ! -f /var/lib/csweet/office/runtime-host.key ]; then
  umask 007
  dd if=/dev/urandom bs=32 count=1 2>/dev/null | base64 > /var/lib/csweet/office/runtime-host.key
fi
chown root:csweet-runtime /var/lib/csweet/office/runtime-host.key
chmod 0640 /var/lib/csweet/office/runtime-host.key
/opt/csweet/office/CSweet.Office.Node \
  --initialize-headquarters-assignment-trust "$control_plane" \
  /var/lib/csweet/office/authorization/headquarters-trust.json
chown root:root /var/lib/csweet/office/authorization/headquarters-trust.json
chmod 0600 /var/lib/csweet/office/authorization/headquarters-trust.json
install -o csweet-node -g csweet-node -m 0600 /dev/null /var/lib/csweet/office/node/enrollment.secret
printf '%s' "$token" > /var/lib/csweet/office/node/enrollment.secret
unset token
cat > /etc/csweet/office.env <<EOF
CSweet__Office__Node__ControlPlaneUrl=$control_plane
CSweet__Office__Node__StateDirectory=/var/lib/csweet/office/node
CSweet__Office__Node__ArtifactCacheDirectory=/var/lib/csweet/office/node/artifact-cache
CSweet__Office__Node__ArtifactMediaDirectory=/var/lib/csweet/artifact-media
CSweet__Office__Node__EnrollmentTokenFilePath=/var/lib/csweet/office/node/enrollment.secret
CSweet__Office__Node__SecurityProfile=$security_profile
CSweet__Office__Node__MixedUseHost=$mixed_use
CSweet__Office__Node__AllowDevelopmentAssignments=$allow_development
CSweet__Office__RuntimeHost__UnixSocketPath=/run/csweet/csweet-office-runtime-v1.sock
CSweet__Office__RuntimeHost__Authentication__SharedKeyFilePath=/var/lib/csweet/office/runtime-host.key
CSweet__Office__RuntimeHost__Authorization__StateDirectory=/var/lib/csweet/office/authorization
EOF
chmod 0600 /etc/csweet/office.env
cat > /etc/csweet/runtime-host.env <<EOF
CSWEET_FIRECRACKER_DATA_ROOT=/var/lib/csweet/office/firecracker
CSWEET_FIRECRACKER_PACKAGE_ROOT=/opt/csweet/office/firecracker
CSWEET_FIRECRACKER_WORKLOAD_UID=$vm_uid
CSWEET_FIRECRACKER_WORKLOAD_GID=$vm_gid
CSWEET_FIRECRACKER_GUEST_VSOCK_PORT=5000
CSWEET_ARTIFACT_MEDIA_ROOT=/var/lib/csweet/artifact-media
EOF
chmod 0600 /etc/csweet/runtime-host.env
install -m 0644 "$package_root/csweet-office-runtime.service" /etc/systemd/system/csweet-office-runtime.service
install -m 0644 "$package_root/csweet-office-node.service" /etc/systemd/system/csweet-office-node.service
systemctl daemon-reload
systemctl enable --now csweet-office-runtime.service csweet-office-node.service
write_result completed
trap - 0
