#!/bin/bash
# Smoke test for the developer guest. Release certification uses the Office hardware runner.
set -euo pipefail
if [[ $# != 1 || ! -f "$1/vmlinux" || ! -f "$1/initrd.img" || ! -f "$1/csweet-agent-guest.img" ]]; then
    echo "Usage: $0 GUEST_OUTPUT_DIRECTORY" >&2
    exit 2
fi
guest=$(cd "$1" && pwd -P)
root=$(cd "$(dirname "$0")/../.." && pwd -P)
helper="$root/src/CSweet.Office.Runtime.AppleVirtualization.Helper"
sources="$helper/Sources/CSweetAppleVirtualizationHelper"
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
swiftc -target "$(uname -m)-apple-macos14.0" "$sources/Models.swift" "$sources/LinuxGuestConfiguration.swift" \
    "$root/scripts/macos/GuestBootProbe.swift" -o "$work/probe"
codesign --force --sign - --entitlements "$helper/CSweet.AppleVirtualization.entitlements" "$work/probe"
/usr/sbin/mkfile -n 1024m "$work/scratch.raw"
"$work/probe" "$guest/vmlinux" "$guest/csweet-agent-guest.img" "$work/scratch.raw"
