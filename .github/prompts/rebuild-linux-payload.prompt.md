---
description: "Rebuild the Linux Firecracker payload using the developer test path: guest rootfs build, Firecracker download and checksum, smoke certification, OpenSSL development signer, and payload assembly."
mode: agent
---

# Rebuild the Linux payload

Use this after changing guest code (which requires a new guest image) or after changing host code (which does
not).

## Steps

1. Confirm the host is ready. You need root, Ubuntu 24.04, cgroup v2, and a readable and writable `/dev/kvm`.

   ```bash
   ls /sys/fs/cgroup/cgroup.controllers
   test -r /dev/kvm && test -w /dev/kvm && echo "kvm ok"
   ```

2. Build the guest rootfs, certify it, sign it, and assemble the payload in one pass:

   ```bash
   sudo ./scripts/linux/initialize-firecracker-test.sh --control-plane https://YOUR-C-SWEET-HOST
   ```

   The script downloads Firecracker and its published `.sha256.txt`, verifies the checksum, enables the
   `+cpu +memory +pids` cgroup controllers, builds the guest ext4 image, publishes the helper and the smoke
   test for the host RID, runs the smoke test, asserts every evidence check is `true`, generates a self-signed
   development signer with OpenSSL, and calls `new-runtime-payload.sh`.

   Output lands in `artifacts/linux-test/<timestamp>-<hex>/`.

3. To rebuild only the payload against an existing certified image, call the payload script directly:

   ```bash
   ./scripts/linux/new-runtime-payload.sh OUTPUT_ROOT linux-x64 \
       FIRECRACKER JAILER VMLINUX INITRD GUEST_EXT4 GUEST_SIG \
       SIGNING_CERT CERT_THUMBPRINT EVIDENCE SUITE_VERSION CERTIFIED_AT [EXPIRES_AT]
   ```

   The output root must be empty or nonexistent. `firecracker --version` and `jailer --version` must match and
   be at least `1.14.0`. `jq` and `sha256sum` must be on `PATH`.

4. Install, unless you passed `--skip-install`. The installer will prompt for the enrollment token; there is no
   flag for it, deliberately.

## Traps

- The installer refuses to upgrade unless the office is draining with zero active assignments.
- `new-native-packages.sh` can emit `deb` and `rpm`, but the release pipeline ships **deb only** and a test
  asserts it.
- A guest-image change invalidates certification, so re-run the smoke path rather than reusing evidence.

Reference: [`docs/50-development/05-linux-dev-loop.md`](../../docs/50-development/05-linux-dev-loop.md),
[`docs/60-release/03-certification.md`](../../docs/60-release/03-certification.md).
