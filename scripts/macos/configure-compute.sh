#!/bin/bash
set -euo pipefail
if [[ $(id -u) != 0 || $# != 3 ]]; then
    echo "Usage: sudo csweet-configure-compute BUSINESS_UUID SIGNED_COMPUTE_IMG DETACHED_SIGNATURE" >&2
    exit 2
fi
[[ $1 =~ ^[[:xdigit:]]{8}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{12}$ ]]
[[ -f $2 && -f $3 ]]
. /usr/local/sbin/csweet-office-storage.sh
office_load_storage startup
umask 077
auth="$office_data_root/authorization"
[[ -d "$auth" && -f "$auth/headquarters-trust.json" ]]
mkdir -p "$office_data_root/compute-images"
stage=$(mktemp -d "$office_data_root/compute-images/.prepare.XXXXXXXX")
policy=$(mktemp "$auth/.compute-policy.XXXXXXXX")
trap 'rm -rf -- "$stage"; rm -f -- "$policy"' EXIT
install -m 0400 "$2" "$stage/compute.img"
install -m 0400 "$3" "$stage/compute.img.sig"
digest=$(shasum -a 256 "$stage/compute.img" | cut -d' ' -f1)
target="$office_data_root/compute-images/$digest"
if [[ -e "$target" ]]; then
    cmp "$stage/compute.img" "$target/compute.img"
    cmp "$stage/compute.img.sig" "$target/compute.img.sig"
else
    mv "$stage" "$target"
fi
/usr/bin/plutil -create xml1 "$policy"
for field in "OrganizationId=$1" "ImageDigest=sha256:$digest" "ImagePath=$target/compute.img" "SignaturePath=$target/compute.img.sig"; do
    /usr/bin/plutil -insert "${field%%=*}" -string "${field#*=}" "$policy"
done
/usr/bin/plutil -insert Enabled -bool YES "$policy"
/usr/bin/plutil -insert MaximumEnvironments -integer 1 "$policy"
/usr/bin/plutil -insert CpuCount -integer 2 "$policy"
/usr/bin/plutil -insert MemoryMiB -integer 2048 "$policy"
/usr/bin/plutil -insert DiskMiB -integer 20480 "$policy"
/usr/bin/plutil -insert Features -json '["docker"]' "$policy"
/usr/bin/plutil -convert json "$policy"
mv -f "$policy" "$auth/compute-policy.json"
echo "Compute policy saved for sha256:$digest. Restart RuntimeHost after draining this Office."
