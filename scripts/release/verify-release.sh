#!/bin/sh
set -eu
[ "$#" -eq 1 ] || { echo "usage: $0 RELEASE_DIRECTORY" >&2; exit 2; }
root=$1
for required in office-release.json SHA256SUMS; do
  [ -s "$root/$required" ] || { echo "Missing release evidence: $required" >&2; exit 2; }
done
(cd "$root" && sha256sum --check SHA256SUMS)
python3 -m json.tool "$root/office-release.json" >/dev/null
find "$root" -maxdepth 1 -name '*.spdx.json' -type f -print -quit | grep -q . || {
  echo "No SPDX SBOM was produced." >&2; exit 2;
}
for asset in "$root"/*.msi "$root"/*.deb "$root"/*.rpm "$root"/*.pkg; do
  [ -e "$asset" ] || continue
  grep -F "$(basename "$asset")" "$root/office-release.json" >/dev/null || {
    echo "Manifest does not list $(basename "$asset")" >&2; exit 2;
  }
done
