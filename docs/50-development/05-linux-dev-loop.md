# Linux development loop

**Audience:** a contributor on a Linux host with hardware virtualization. This loop builds a Firecracker
guest, certifies it against real no-network VMs, signs it with a development key, and optionally installs
the result.

```bash
sudo ./scripts/linux/initialize-firecracker-test.sh --control-plane https://your-office.control.example
```

The payload is what the installer consumes; the loop never installs silently, and `--skip-install` makes
that explicit.

## Prerequisites

| Requirement | Detail |
|---|---|
| `root` | The script refuses to run for a non-root user. |
| Commands on `PATH` | `curl`, `dotnet`, `jq`, `openssl`, `sha256sum`, `tar`. |
| cgroup v2 | `/sys/fs/cgroup/cgroup.controllers` must be readable. |
| KVM | `/dev/kvm` must be readable and writable. |
| Architecture | `x86_64` or `aarch64`; anything else is rejected with a message. |
| Guest builder only | `chroot`, `debootstrap`, `e2fsck`, `mkfs.ext4`, `resize2fs`, and a `dotnet` installation that contains the .NET 10 SDK. |
| Native build host | The guest builder requires the host architecture to match the requested RID. |

## Parameters

| Parameter | Default | Effect |
|---|---|---|
| `--control-plane <url>` | — | Required unless `--skip-install`; must start with `https://`. Passed to `install-office.sh`. |
| `--output-root <path>` | `<repo>/artifacts/linux-test` | Root for runs, the tool cache, and guest artifacts. May not be `/`. |
| `--firecracker-version <vX.Y.Z>` | `v1.16.1` | Must match `v<digits>.<digits>.<digits>`. Selects the release to download and the tool cache key. |
| `--skip-install` | off | Stops after payload creation. |

`--skip-install` without `--control-plane` is the fully offline path: build, certify, sign, assemble, stop.

## What the run does, in order

1. Checks the layout: run root `<output-root>/<yyyyMMdd-HHMMSS>-<8-hex>`, tool cache
   `<output-root>/tools/<firecracker-version>-<architecture>`, archive cache `<output-root>/cache`.
2. Acquires Firecracker and jailer for the pinned version:
   - downloads `firecracker-<version>-<arch>.tgz` and its published `.sha256.txt` with
     `--proto '=https' --tlsv1.2` into the cache when they are not already there;
   - verifies the archive with `sha256sum --check --status` against the published checksum and aborts on
     mismatch;
   - extracts into a temporary directory and installs only `firecracker` and `jailer` with mode `0755`;
   - prints both `--version` outputs.
3. Builds the immutable guest filesystem by calling `scripts/linux/new-firecracker-guest.sh` (below).
4. Publishes the Firecracker helper and the certification smoke runner `-c Release --self-contained true
   -p:PublishSingleFile=true -p:DebugType=None`, and assembles a package directory containing
   `firecracker`, `jailer`, `vmlinux`, and `initrd.img`.
5. Creates the `csweet-vm` system user when missing and exports
   `CSWEET_FIRECRACKER_PACKAGE_ROOT`, `CSWEET_FIRECRACKER_WORKLOAD_UID`, `CSWEET_FIRECRACKER_WORKLOAD_GID`,
   and `CSWEET_FIRECRACKER_PARENT_CGROUP=csweet-certification`, creating the parent cgroup and delegating
   `cpu`, `memory`, and `pids` where the host supports it.
6. Runs the certification smoke test against real no-network guests:

   ```
   CSweet.Office.WindowsSmokeTest --provider firecracker --helper <helper> \
       --guest-image <guest>/csweet-agent-guest.ext4 --probe <guest>/CSweet.Office.GuestProbe \
       --output-root <smoke>/output --evidence <run-root>/linux-firecracker.json
   ```

   and then requires `jq -e '.checks | length > 0 and all(.[]; . == true)'` on the evidence file.
7. Creates an OpenSSL development signer and signs the guest image:

   ```
   openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 365 \
       -subj "/CN=C-Sweet Linux Firecracker Development Guest Signer" ...
   ```

   The private key is written mode `0600`, the certificate is converted to DER for the payload, the image
   is signed with `openssl dgst -sha256 -sign`, and the thumbprint is read back with
   `openssl x509 -noout -fingerprint -sha1`.
8. Calls `scripts/linux/new-runtime-payload.sh` with the certified inputs.
9. Unless `--skip-install` was passed, runs `install-office.sh <payload-root> <control-plane-url>`, which
   prompts for the one-use enrollment token. The token is never passed as an argument.

Outputs at the end of a run: the evidence file (`linux-firecracker.json`) and the payload root, both
printed. The payload contains `CSweet.Office.RuntimeHost`, `CSweet.Office.Node`, the helper, the
Firecracker binaries, `vmlinux`, `initrd.img`, the image, its signature, the signing certificate, the
evidence, both service units, the install and uninstall scripts, and `runtime-manifest.json` with
`providerId: "firecracker-kvm"`.

## Rebuilding in parts

There is no guest image cache on this platform. Every full run rebuilds the guest from `debootstrap`
upward. When that is not what you want, run the individual steps.

### Guest image only

```bash
sudo ./scripts/linux/new-firecracker-guest.sh <EMPTY-OUTPUT-DIR> linux-x64
```

- Usage: `new-firecracker-guest.sh OUTPUT_ROOT RID [UBUNTU_SUITE] [UBUNTU_MIRROR]`; `RID` is `linux-x64`
  or `linux-arm64`, the suite defaults to `noble`, and the mirror defaults to the architecture's Ubuntu
  archive.
- Runs as root and needs `chroot`, `debootstrap`, `dotnet`, `e2fsck`, `mkfs.ext4`, and `resize2fs`.
- The output directory must be empty; the script refuses otherwise.
- Publishes `RuntimeGuest`, `BuilderGuest`, `ToolchainGuest`, and `GuestProbe` as self-contained
  single-file linux executables, copies the host's .NET root into the image, and verifies that a .NET 10
  SDK is present before producing the kernel, initrd, probe, and `csweet-agent-guest.ext4`.
- Prints the guest digest. The image is not signed and has no certification evidence; both come from a
  full loop run or from the steps below.

### Payload only

`new-runtime-payload.sh` is fully positional and needs an existing evidence file:

```
usage: new-runtime-payload.sh OUTPUT_ROOT RID FIRECRACKER JAILER VMLINUX INITRD GUEST_EXT4 \
           GUEST_SIG SIGNING_CERT CERT_THUMBPRINT EVIDENCE SUITE_VERSION CERTIFIED_AT [EXPIRES_AT]
```

It requires `dotnet`, `jq`, and `sha256sum`, an output directory that is empty, a hexadecimal thumbprint,
and a `firecracker` and `jailer` that are executable and from the same release — and that release must be
`1.14.0` or later, because bounded serial diagnostics are required. To rerun it against a previous run
root, reuse the tool cache for `FIRECRACKER`/`JAILER` (`<output-root>/tools/<version>-<arch>`), the guest
tree for `VMLINUX`, `INITRD`, and `GUEST_EXT4` (`<run-root>/guest`), and the run root for the signature,
certificate, and evidence (`linux-firecracker.json`, `SUITE_VERSION` and `CERTIFIED_AT` read from it with
`jq`).

> **Out of repo:** the Firecracker release archive and its published checksum come from the upstream
> project. The loop verifies the checksum but does not re-sign or vendor the binaries.

## Footguns

| Footgun | Consequence |
|---|---|
| Output directories must be empty | Both the guest builder and the payload builder refuse a non-empty output root, so an interrupted run needs a new directory rather than a retry in place. |
| Guest builder is native-architecture only | A cross-architecture guest build is rejected before anything is published. |
| Firecracker and jailer versions must be equal | A mixed tool set fails the payload build, and the provider probe independently requires equal versions before a payload is considered available ([07-provider-backends.md](../30-workloads/07-provider-backends.md)). |
| The OpenSSL signer is development-only | Development payloads are expected to fail the release signing and certification path; see `AGENTS.md`. |
| No cache exists | A guest-source change costs a full rebuild every time; the Windows loop's fingerprint cache has no Linux equivalent. |
| The guest builder copies the host's .NET installation | The image contains whatever SDK the host has, and the builder fails after `chroot` when that copy does not contain a .NET 10 SDK. |
| Cached archives are still verified | Every run re-runs `sha256sum --check` against the published checksum, so a corrupted cache fails loudly rather than being trusted. |

## Sources

`scripts/linux/initialize-firecracker-test.sh`, `scripts/linux/new-firecracker-guest.sh`,
`scripts/linux/new-runtime-payload.sh`, `scripts/linux/install-office.sh`, `build/linux-firecracker/provision-guest.sh`,
`src/CSweet.Office.WindowsSmokeTest/Program.cs`, `src/CSweet.Office.Runtime.Firecracker.Helper/HelperArguments.cs`.

Verified: 2026-09-15.
