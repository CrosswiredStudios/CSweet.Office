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
storage_volume=
gateway_sha256=
cpu=
memory=
disk=
fresh=false
while [ "$#" -gt 0 ]; do
  if [ "$1" = --limits ]; then
    [ "$#" -ge 4 ] || exit 2
    for limit in "$2" "$3" "$4"; do
      case "$limit" in ''|*[!0-9]*) exit 2 ;; esac
      [ "${#limit}" -le 10 ] && [ "$limit" -gt 0 ] && [ "$limit" -le 2147483647 ] || exit 2
    done
    cpu=$2; memory=$3; disk=$4; shift 4; continue
  fi
  case "$1" in
    --fresh-only) fresh=true; shift ;;
    --gateway-sha256) [ "$#" -ge 2 ] || exit 2; gateway_sha256=$2; shift 2 ;;
    --storage-volume) [ "$#" -ge 2 ] || exit 2; storage_volume=$2; shift 2 ;;
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

set -- --storage-volume "$storage_volume"
if [ -n "$gateway_sha256" ]; then set -- "$@" --gateway-sha256 "$gateway_sha256"; fi
if [ -n "$cpu" ]; then set -- "$@" --limits "$cpu" "$memory" "$disk"; fi
if [ "$fresh" = true ]; then set -- "$@" --fresh-only; fi
if [ "$dedicated" = true ]; then
  echo "Security label: Hardened (dedicated-host declaration)."
  echo "Keep Ubuntu, KVM, Firecracker, firmware, and CPU microcode patched."
  exec "$installer" "$package_root" "$control_plane" --security-profile hardened --dedicated-host "$@"
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
exec "$installer" "$package_root" "$control_plane" --security-profile baseline "$@"
