#!/usr/bin/env bash
# GitHub release assets are individually limited to 2 GiB. The release owner
# pins the digest of the complete VHDX independently of its transport parts.
set -euo pipefail
disk=$1
[[ ! -e "$disk" && "$IMAGE_URL" == https://* && "$IMAGE_SHA256" =~ ^[0-9a-f]{64}$ ]] || exit 1
download=(curl --fail --location --proto '=https' --proto-redir '=https' --retry 3)
if [[ -z ${IMAGE_PARTS:-} ]]; then
    "${download[@]}" --max-filesize 17179869184 --output "$disk" -- "$IMAGE_URL"
else
    [[ "$IMAGE_PARTS" =~ ^([2-9]|1[0-6])$ && "$IMAGE_URL" == *.vhdx.part001 ]] || exit 1
    part=$(mktemp)
    trap 'rm -f -- "$part"' EXIT
    for ((i=1; i<=IMAGE_PARTS; i++)); do
        printf -v suffix '%03d' "$i"
        "${download[@]}" --max-filesize 1073741824 --output "$part" -- "${IMAGE_URL%001}$suffix"
        size=$(stat --format=%s "$part")
        (( size > 0 && size <= 1073741824 && (i == IMAGE_PARTS || size == 1073741824) )) || exit 1
        cat "$part" >> "$disk"
    done
fi
printf '%s  %s\n' "$IMAGE_SHA256" "$disk" | sha256sum --check
