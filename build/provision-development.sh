#!/usr/bin/env bash
set -euo pipefail
payload=${1:-/tmp/csweet-image-payload}
arch=$(tr -d '\r\n' < "$payload/development-architecture")
case "$arch" in
  x64) machine=x86_64; tun_entry=tun2socks-linux-amd64; mirror=https://archive.ubuntu.com/ubuntu ;;
  arm64) machine=aarch64; tun_entry=tun2socks-linux-arm64; mirror=https://ports.ubuntu.com/ubuntu-ports ;;
  *) echo 'Unsupported development payload architecture' >&2; exit 2 ;;
esac
test "$(uname -m)" = "$machine"
# The dated Ubuntu snapshot pins distribution dependencies and their signed archive metadata.
cat >/etc/apt/sources.list.d/ubuntu.sources <<SOURCES
Types: deb
URIs: $mirror
Suites: noble noble-updates noble-security
Components: main universe
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
Snapshot: 20261012T000000Z
SOURCES
apt-get -o Acquire::Check-Valid-Until=false -o Acquire::Retries=3 -o APT::Update::Error-Mode=any update
DEBIAN_FRONTEND=noninteractive apt-get -o Acquire::Check-Valid-Until=false -o Acquire::Retries=3 \
  --snapshot 20261012T000000Z install -y --no-install-recommends \
  bash python3.12 python3.12-venv git curl ca-certificates build-essential pkg-config ripgrep openssh-client iproute2 unzip xz-utils
python3.12 - "$payload" "$arch" <<'PYTHON'
import hashlib,json,pathlib,sys
root=pathlib.Path(sys.argv[1]); lock=json.loads((root/'development-tools.lock.json').read_text()); a=lock['architectures'][sys.argv[2]]
for name,algorithm,key in [('node.tar.xz','sha256','nodeSha256'),('dotnet.tar.gz','sha512','dotnetSha512'),('tun2socks.zip','sha256','tunSha256')]:
 h=hashlib.new(algorithm)
 with (root/name).open('rb') as f:
  for chunk in iter(lambda:f.read(1024*1024),b''): h.update(chunk)
 if h.hexdigest()!=a[key]: raise SystemExit('Pinned development artifact failed verification: '+name)
PYTHON
tar -xJf "$payload/node.tar.xz" --strip-components=1 -C /usr/local
install -d -m 0755 /usr/share/dotnet /usr/lib/csweet
tar -xzf "$payload/dotnet.tar.gz" -C /usr/share/dotnet
ln -sfn /usr/share/dotnet/dotnet /usr/local/bin/dotnet
unzip -p "$payload/tun2socks.zip" "$tun_entry" >/usr/lib/csweet/tun2socks
chmod 0755 /usr/lib/csweet/tun2socks
test "$(node --version)" = 'v22.23.3'
test "$(npm --version)" = '10.9.9'
dotnet --list-sdks | grep -q '^10.0.401 '
python3.12 --version
git --version
curl --version >/dev/null
cp "$payload/development-tools.lock.json" /usr/lib/csweet/development-tools.lock.json
dpkg-query -W -f='${Package}=${Version}\n' | LC_ALL=C sort >/usr/lib/csweet/development-distro.lock
install -d -m 0755 /etc/modules-load.d
printf 'tun\n' >/etc/modules-load.d/csweet-development-tun.conf
ln -sfn /run/csweet/resolv.conf /etc/resolv.conf
apt-get clean
