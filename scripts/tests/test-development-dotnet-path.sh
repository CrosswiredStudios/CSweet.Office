#!/usr/bin/env bash
# Run only in a disposable SDK container: this replaces that container's SDK tree.
# docker run --rm --network none -v "$PWD:/repo:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash /repo/scripts/tests/test-development-dotnet-path.sh
set -euo pipefail
test -f /.dockerenv || { echo 'This test requires a disposable Docker container.' >&2; exit 2; }
root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
payload=$(mktemp -d)
sdk_root=$(dirname "$(readlink -f "$(command -v dotnet)")")
test "$sdk_root" = /usr/share/dotnet
tar -czf "$payload/dotnet.tar.gz" -C "$sdk_root" .
mv /usr/share/dotnet "$payload/original-sdk"
rm -f /usr/bin/dotnet /usr/local/bin/dotnet

# Execute the actual provisioning block; a clean hosted Ubuntu image has no
# distro SDK launcher. Both the absolute certification path and PATH must work.
block=$(sed -n '/^install -d -m 0755 \/usr\/share\/dotnet /,/^unzip /p' "$root/build/provision-development.sh" | sed '$d')
test -n "$block"
eval "$block"
test "$(readlink -f /usr/bin/dotnet)" = /usr/share/dotnet/dotnet || {
  echo 'Certification launcher /usr/bin/dotnet does not resolve to the installed SDK.' >&2; exit 1;
}
test "$(readlink -f /usr/local/bin/dotnet)" = /usr/share/dotnet/dotnet
runuser -u nobody -- /usr/bin/dotnet --list-sdks | grep -E '^10\.'
runuser -u nobody -- /usr/local/bin/dotnet --list-sdks | grep -E '^10\.'
echo 'Clean-image SDK provisioning passes the unprivileged certification path.'
