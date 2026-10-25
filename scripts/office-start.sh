#!/bin/sh
set -eu
helper=$(dirname "$0")/csweet-office-storage.sh
. "$helper"
office_load_storage startup || exit 2
if [ "$office_platform" = linux ] && [ ! -w "$office_data_root" ]; then echo "Office data storage is not writable." >&2; exit 2; fi
case "${1:-}" in
  --verify) exit 0 ;;
  runtime) exec "$office_install_root/CSweet.Office.RuntimeHost" ;;
  node) exec "$office_install_root/CSweet.Office.Node" ;;
  *) exit 2 ;;
esac
