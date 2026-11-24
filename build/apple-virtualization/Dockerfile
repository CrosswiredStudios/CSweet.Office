# Build on the Linux architecture that will run in the target Mac VM.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS publish
WORKDIR /src
COPY Directory.* Office.GlobalUsings.cs ./
COPY src/ ./src/
ARG TARGETARCH
RUN set -eu; case "$TARGETARCH" in arm64) rid=linux-arm64 ;; amd64) rid=linux-x64 ;; *) exit 2 ;; esac; \
    for project in RuntimeGuest BuilderGuest ToolchainGuest GuestProbe; do \
      dotnet publish "src/CSweet.Office.$project" -c Release -r "$rid" --self-contained true \
        -p:UseLocalOfficeContracts=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=None -o "/publish/$project"; \
    done

FROM ubuntu:24.04 AS guest
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      systemd-sysv udev kmod initramfs-tools linux-image-virtual e2fsprogs util-linux \
      git ca-certificates libicu74 libssl3t64 zlib1g && rm -rf /var/lib/apt/lists/*
COPY --from=publish /usr/share/dotnet /usr/share/dotnet
COPY --from=publish /publish/RuntimeGuest/CSweet.Office.RuntimeGuest /tmp/
COPY --from=publish /publish/BuilderGuest/CSweet.Office.BuilderGuest /tmp/
COPY --from=publish /publish/ToolchainGuest/CSweet.Office.ToolchainGuest /tmp/
COPY build/linux-firecracker/provision-guest.sh /tmp/provision-guest.sh
RUN ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet && /bin/bash /tmp/provision-guest.sh && \
    rm /tmp/provision-guest.sh && dotnet --list-sdks

FROM ubuntu:24.04 AS disk
RUN apt-get update && apt-get install -y --no-install-recommends e2fsprogs gzip && rm -rf /var/lib/apt/lists/*
COPY --from=guest / /rootfs/
ARG TARGETARCH
RUN set -eu; mkdir /output; \
    kernel=$(ls /rootfs/boot/vmlinuz-*); version=${kernel##*/vmlinuz-}; \
    if [ "$TARGETARCH" = arm64 ] && gzip -t "$kernel" 2>/dev/null; then gzip -dc "$kernel" >/output/vmlinux; \
    else cp "$kernel" /output/vmlinux; fi; \
    cp "/rootfs/boot/initrd.img-$version" /output/initrd.img; \
    rm -rf /rootfs/boot/*; \
    size=$(du -sm /rootfs | cut -f1); truncate -s "$((size + 512))M" /output/csweet-agent-guest.img; \
    mkfs.ext4 -q -F -L CSWEET_ROOT -d /rootfs /output/csweet-agent-guest.img; \
    e2fsck -fn /output/csweet-agent-guest.img; chmod 0444 /output/*
FROM scratch AS artifacts
COPY --from=disk /output/ /
COPY --from=publish /publish/GuestProbe/CSweet.Office.GuestProbe /
