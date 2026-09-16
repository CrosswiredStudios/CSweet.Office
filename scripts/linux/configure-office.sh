#!/bin/sh
set -eu

package_root=/usr/lib/csweet/office-installer
installer=$package_root/install-office.sh

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this command with sudo." >&2
  exit 1
fi
if [ "$#" -lt 1 ]; then
  echo "usage: sudo $0 https://control-plane [--dedicated-host] [--accept-baseline-risk]" >&2
  exit 2
fi
control_plane=$1
shift
dedicated=false
accepted=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --dedicated-host) dedicated=true; shift ;;
    --accept-baseline-risk) accepted=true; shift ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
case "$control_plane" in
  https://*) ;;
  *) echo "Control-plane URL must use HTTPS." >&2; exit 2 ;;
esac
if [ ! -x "$installer" ]; then
  echo "The signed C-Sweet Office payload is not installed." >&2
  exit 2
fi

if [ "$dedicated" = true ]; then
  echo "Security label: Hardened (dedicated-host declaration)."
  echo "Keep Ubuntu, KVM, Firecracker, firmware, and CPU microcode patched."
  exec "$installer" "$package_root" "$control_plane" --security-profile hardened --dedicated-host
fi

echo "Security label: Baseline (shared or personal host)."
echo "Running C-Sweet and Office on this machine is supported. Agents run in isolated Firecracker VMs,"
echo "but a vulnerability in the host OS, KVM, or Firecracker could affect other data on this machine."
echo "Use a dedicated, patched host for stronger isolation. This label is informational and does not disable agents."
if [ "$accepted" != true ]; then
  if [ ! -t 0 ]; then
    echo "Non-interactive setup requires --accept-baseline-risk." >&2
    exit 2
  fi
  printf 'Continue with Baseline setup? [y/N] ' >&2
  IFS= read -r answer
  case "$answer" in y|Y|yes|YES) ;; *) echo "Setup cancelled." >&2; exit 1 ;; esac
fi
exec "$installer" "$package_root" "$control_plane" --security-profile baseline
