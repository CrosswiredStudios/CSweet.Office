#!/bin/bash
set -euo pipefail
[[ $# == 2 && $(id -u) == 0 && "$2" =~ ^[0-9a-f]{64}$ ]]
umask 077
ulimit -c 0
work=$(mktemp -d /private/tmp/csCm.XXXXXXXX)
cleanup() {
    # A failed assertion can leave a VM manager holding its private disk.
    while read -r pid; do
        [[ -z "$pid" ]] || kill "$pid" 2>/dev/null || true
    done < <(ps -axo pid=,command= | awk -v bin="$work/helper" '$2 == bin && $3 == "--workload-host" { print $1 }')
    rm -rf -- "$work"
}
trap cleanup EXIT
cp -c "$1" "$work/input.tar"
cd "$work"
printf '%s  input.tar\n' "$2" | shasum -a 256 -c -
tar --no-same-owner -xf input.tar
chmod -R go-w "$work"
codesign --verify --strict "$work/helper"
export CSWEET_APPLE_VIRTUALIZATION_PACKAGE_ROOT="$work"
export CSWEET_APPLE_VIRTUALIZATION_DATA_ROOT="$work/data"
export CSWEET_APPLE_VIRTUALIZATION_SOCKET_ROOT="$work/sockets"
export CSWEET_ARTIFACT_MEDIA_ROOT="$work/media"
export DOTNET_CLI_HOME="$work"
export DOTNET_EnableDiagnostics=0
/usr/local/share/dotnet/dotnet "$work/runner/CSweet.Office.MacComputeSmokeTest.dll" "$work"
