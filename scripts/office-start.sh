#!/bin/sh
set -eu
helper=$(dirname "$0")/csweet-office-storage.sh
. "$helper"
office_load_storage startup || exit 2
if [ "$office_platform" = linux ] && [ ! -w "$office_data_root" ]; then echo "Office data storage is not writable." >&2; exit 2; fi
case "${1:-}" in
  --verify) exit 0 ;;
  runtime)
    if [ "$office_platform" = macos ]; then
      # /var/run is volatile. The socket inherits the protected directory's group on macOS.
      install -d -o root -g _csweet -m 0750 /var/run/csweet-office
    fi
    exec "$office_install_root/CSweet.Office.RuntimeHost" ;;
  node) exec "$office_install_root/CSweet.Office.Node" ;;
  *) exit 2 ;;
esac
