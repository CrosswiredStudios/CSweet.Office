#!/usr/bin/env bash
# Build without Hyper-V on an Ubuntu GitHub runner. Certification happens on the destination host.
set -euo pipefail
root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
output=$(realpath -m "$1")
mkdir -p "$output"
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
base=https://cloud-images.ubuntu.com/releases/noble/release-20260911
image=ubuntu-24.04-server-cloudimg-amd64.img
curl --fail --location --proto '=https' "$base/$image" -o "$work/$image"
curl --fail --location --proto '=https' "$base/SHA256SUMS" -o "$work/SHA256SUMS"
(cd "$work"; grep " $image$\| \*$image$" SHA256SUMS > image.sha256; test -s image.sha256; sha256sum -c image.sha256)
mkdir "$work/csweet-image-payload"
for project in CSweet.Office.RuntimeGuest CSweet.Office.BuilderGuest CSweet.Office.ToolchainGuest; do
  dotnet publish "$root/src/$project/$project.csproj" -c Release -r linux-x64 --self-contained true \
    -p:UseLocalOfficeContracts=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "$work/$project"
  cp "$work/$project/$project" "$work/csweet-image-payload/$project.bin"
done
# The cloud image has a signed Ubuntu EFI boot chain suitable for Hyper-V Generation 2.
export LIBGUESTFS_BACKEND=direct
qemu-img create -f qcow2 "$work/expanded.qcow2" 12G
virt-resize --expand /dev/sda1 "$work/$image" "$work/expanded.qcow2"
virt-customize -a "$work/expanded.qcow2" --memsize 4096 \
  --install dotnet-sdk-10.0,linux-tools-virtual,linux-cloud-tools-virtual \
  --copy-in "$work/csweet-image-payload:/tmp" \
  --upload "$root/build/windows-hyperv/provision-guest.sh:/tmp/csweet-provision-guest.sh" \
  --run-command '/bin/bash /tmp/csweet-provision-guest.sh' \
  --delete /tmp/csweet-provision-guest.sh \
  --run-command 'systemctl mask ssh.service ssh.socket && passwd -l root && if getent passwd ubuntu >/dev/null; then passwd -l ubuntu; fi' \
  --run-command 'rm -f /etc/ssh/ssh_host_* /root/.ssh/authorized_keys /home/ubuntu/.ssh/authorized_keys; rm -rf /tmp/csweet-image-payload; apt-get clean; sync'
qemu-img convert -O vhdx -o subformat=dynamic "$work/expanded.qcow2" "$output/csweet-agent-guest.vhdx"
