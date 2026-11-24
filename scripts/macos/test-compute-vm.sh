#!/bin/bash
# Run locally built code against a disposable, root-owned Office instance.
set -euo pipefail
if [[ $# != 3 || $(id -u) == 0 ]]; then
  echo "Usage: $0 COMPUTE_GUEST_DIRECTORY OFFICE_AGENT_GUEST_DIRECTORY OFFICE_HARDWARE_EVIDENCE_JSON" >&2
  exit 2
fi
repo=$(cd "$(dirname "$0")/../.." && pwd -P)
h="$repo/src/CSweet.Office.Runtime.AppleVirtualization.Helper"
stage=$(mktemp -d)
trap 'rm -rf -- "$stage"' EXIT
mkdir "$stage/input"
cmp "$1/vmlinux" "$2/vmlinux"
cp -c "$1/compute.img" "$stage/input/"
cp -c "$2/csweet-agent-guest.img" "$stage/input/agent.img"
cp "$2/vmlinux" "$2/initrd.img" "$stage/input/"
cp "$3" "$stage/input/evidence.json"
dotnet publish "$repo/tests/CSweet.Office.MacComputeSmokeTest" -c Release -p:UseLocalOfficeContracts=false -o "$stage/input/runner"
swift build --package-path "$h" -c release
cp "$h/.build/release/CSweet.Office.Runtime.AppleVirtualization.Helper" "$stage/input/helper"
codesign --force --sign - --entitlements "$h/CSweet.AppleVirtualization.entitlements" "$stage/input/helper"
COPYFILE_DISABLE=1 tar -cf "$stage/input.tar" -C "$stage/input" .
digest=$(shasum -a 256 "$stage/input.tar" | cut -d' ' -f1)
sudo /bin/bash "$repo/scripts/macos/run-compute-fixture.sh" "$stage/input.tar" "$digest"
