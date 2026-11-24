#!/bin/sh
set -eu

package_root='/Library/Application Support/CSweet/Office/InstallerPayload'
installer="$package_root/install-office.sh"

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this command with sudo." >&2
  exit 1
fi
if [ "$#" -lt 1 ]; then
  echo "usage: sudo $0 https://control-plane [--storage-volume UUID] [--gateway-sha256 HEX]" >&2
  exit 2
fi
case "$1" in
  https://*) ;;
  *) echo "Control-plane URL must use HTTPS." >&2; exit 2 ;;
esac
if [ ! -x "$installer" ]; then
  echo "The signed C-Sweet Office payload is not installed." >&2
  exit 2
fi

control_plane=$1
shift
storage_volume=
gateway_sha256=
while [ "$#" -gt 0 ]; do
  [ "$#" -ge 2 ] || exit 2
  case "$1" in
    --storage-volume) storage_volume=$2 ;;
    --gateway-sha256) gateway_sha256=$2 ;;
    *) echo "Unknown configuration option: $1" >&2; exit 2 ;;
  esac
  shift 2
done
set -- "$package_root" "$control_plane"
if [ -n "$storage_volume" ]; then set -- "$@" --storage-volume "$storage_volume"; fi
if [ -n "$gateway_sha256" ]; then set -- "$@" --gateway-sha256 "$gateway_sha256"; fi
exec "$installer" "$@"
