#!/bin/bash
set -euo pipefail

if [[ $# -lt 2 || $# -gt 4 ]]; then
  echo "usage: $0 OUTPUT_ROOT RID [UBUNTU_SUITE] [UBUNTU_MIRROR]" >&2
  exit 2
fi
if [[ $(id -u) -ne 0 ]]; then
  echo "Run this guest-image builder as root." >&2
  exit 2
fi
for command_name in chroot debootstrap dotnet e2fsck mkfs.ext4 resize2fs mount mountpoint umount; do
  command -v "$command_name" >/dev/null 2>&1 || { echo "$command_name is required." >&2; exit 2; }
done

output_root=$(realpath -m "$1")
runtime_id=$2
ubuntu_suite=${3:-noble}
case "$runtime_id" in
  linux-x64) architecture=amd64; expected_machine=x86_64; default_mirror=http://archive.ubuntu.com/ubuntu ;;
  linux-arm64) architecture=arm64; expected_machine=aarch64; default_mirror=http://ports.ubuntu.com/ubuntu-ports ;;
  *) echo "RID must be linux-x64 or linux-arm64." >&2; exit 2 ;;
esac
ubuntu_mirror=${4:-$default_mirror}
if [[ $(uname -m) != "$expected_machine" ]]; then
  echo "The development guest builder currently requires a native $expected_machine host." >&2
  exit 2
fi
if [[ -e "$output_root" && -n $(find "$output_root" -mindepth 1 -print -quit 2>/dev/null) ]]; then
  echo "The output directory must be empty: $output_root" >&2
  exit 2
fi

script_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_root=$(realpath "$script_root/../..")
build_root=$(mktemp -d)
guest_mounts=()
unmount_guest() {
  local failed=0 index
  for ((index=${#guest_mounts[@]}-1; index>=0; index--)); do
    if mountpoint -q "${guest_mounts[index]}"; then
      umount "${guest_mounts[index]}" || failed=1
    fi
  done
  return "$failed"
}
cleanup() {
  local result=$?
  if unmount_guest; then
    rm -rf -- "$build_root"
  else
    echo "Could not unmount guest filesystems; preserving $build_root for cleanup." >&2
    result=1
  fi
  return "$result"
}
trap cleanup EXIT
rootfs="$build_root/rootfs"
guest_publish="$build_root/guest"
builder_publish="$build_root/builder"
toolchain_publish="$build_root/toolchain"
probe_publish="$build_root/probe"
mkdir -p "$output_root" "$rootfs" "$guest_publish" "$builder_publish" "$toolchain_publish" "$probe_publish"

echo "Publishing the guest broker, agent builder, and certification probe..."
dotnet publish "$repository_root/src/CSweet.Office.RuntimeGuest/CSweet.Office.RuntimeGuest.csproj" \
  -c Release -r "$runtime_id" --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "$guest_publish"
dotnet publish "$repository_root/src/CSweet.Office.BuilderGuest/CSweet.Office.BuilderGuest.csproj" \
  -c Release -r "$runtime_id" --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "$builder_publish"
dotnet publish "$repository_root/src/CSweet.Office.ToolchainGuest/CSweet.Office.ToolchainGuest.csproj" \
  -c Release -r "$runtime_id" --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "$toolchain_publish"
dotnet publish "$repository_root/src/CSweet.Office.GuestProbe/CSweet.Office.GuestProbe.csproj" \
  -c Release -r "$runtime_id" --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "$probe_publish"

echo "Creating the minimal Ubuntu $ubuntu_suite root filesystem..."
debootstrap --arch="$architecture" --variant=minbase --components=main,universe \
  --include=systemd-sysv,udev,e2fsprogs,util-linux,kmod,ca-certificates,libicu74,libssl3t64,zlib1g,initramfs-tools,linux-image-virtual \
  "$ubuntu_suite" "$rootfs" "$ubuntu_mirror"

# Provision in a complete chroot, then unmount before measuring or copying its tree.
for filesystem in proc sys; do
  guest_mounts+=("$rootfs/$filesystem")
done
mount -t proc -o nosuid,nodev,noexec proc "$rootfs/proc"
mount -t sysfs -o ro,nosuid,nodev,noexec sysfs "$rootfs/sys"

dotnet_executable=$(readlink -f "$(command -v dotnet)")
dotnet_root=$(dirname "$dotnet_executable")
install -d -m 0755 "$rootfs/usr/share/dotnet"
cp -a "$dotnet_root/." "$rootfs/usr/share/dotnet/"
ln -s /usr/share/dotnet/dotnet "$rootfs/usr/bin/dotnet"
install -m 0755 "$guest_publish/CSweet.Office.RuntimeGuest" "$rootfs/tmp/CSweet.Office.RuntimeGuest"
install -m 0755 "$builder_publish/CSweet.Office.BuilderGuest" "$rootfs/tmp/CSweet.Office.BuilderGuest"
install -m 0755 "$toolchain_publish/CSweet.Office.ToolchainGuest" "$rootfs/tmp/CSweet.Office.ToolchainGuest"
install -m 0755 "$repository_root/build/linux-firecracker/provision-guest.sh" "$rootfs/tmp/provision-csweet-guest.sh"
chroot "$rootfs" /bin/bash /tmp/provision-csweet-guest.sh
chroot "$rootfs" /usr/bin/dotnet --list-sdks | grep -Eq '^10\.' || {
  echo "The copied development SDK does not contain .NET 10." >&2; exit 2;
}

kernel=$(find "$rootfs/boot" -maxdepth 1 -type f -name 'vmlinuz-*' | sort -V | tail -n 1)
[[ -n "$kernel" && -s "$kernel" ]] || { echo "No guest kernel was installed in /boot." >&2; exit 2; }
kernel_version=${kernel##*/vmlinuz-}
initrd="$rootfs/boot/initrd.img-$kernel_version"
# minbase omits recommended packages. Include initramfs-tools above and explicitly
# generate/update the matching initrd rather than relying on kernel package hooks.
initrd_mode=-c
[[ ! -e "$initrd" ]] || initrd_mode=-u
chroot "$rootfs" /usr/sbin/update-initramfs "$initrd_mode" -k "$kernel_version"
[[ -s "$initrd" ]] || { echo "No initrd was produced for guest kernel $kernel_version." >&2; exit 2; }
unmount_guest
install -m 0644 "$kernel" "$output_root/vmlinux"
install -m 0644 "$initrd" "$output_root/initrd.img"
install -m 0755 "$probe_publish/CSweet.Office.GuestProbe" "$output_root/CSweet.Office.GuestProbe"

used_bytes=$(du -sx --block-size=1 "$rootfs" | awk '{print $1}')
image_bytes=$((used_bytes + 768 * 1024 * 1024))
alignment=$((64 * 1024 * 1024))
image_bytes=$(((image_bytes + alignment - 1) / alignment * alignment))
guest_image="$output_root/csweet-agent-guest.ext4"
truncate -s "$image_bytes" "$guest_image"
mkfs.ext4 -q -F -L CSWEET_ROOT -d "$rootfs" "$guest_image"
e2fsck -fy "$guest_image" >/dev/null || [[ $? -eq 1 ]]
resize2fs -M "$guest_image" >/dev/null
minimum_blocks=$(dumpe2fs -h "$guest_image" 2>/dev/null | awk '/Block count:/ {blocks=$3} /Block size:/ {size=$3} END {print blocks * size}')
truncate -s "$minimum_blocks" "$guest_image"
chmod 0444 "$guest_image" "$output_root/vmlinux" "$output_root/initrd.img"

echo "Firecracker development guest created at $output_root"
echo "Guest digest: sha256:$(sha256sum "$guest_image" | awk '{print $1}')"
