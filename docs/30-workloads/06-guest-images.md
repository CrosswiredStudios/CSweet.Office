# Guest images

**Audience:** contributors changing guest code; release engineers building images.

A guest image is an immutable, signed, certified disk image that a provider boots. Exactly **one image is
certified per provider configuration**, and the certification binds the image digest to the provider, the
helper, and the broker protocol version.

## One image, three runners

All three workload kinds boot the **same** guest image. The kind selects which in-image executable runs:

| Kind | Runner | Installed at |
|---|---|---|
| `0` Builder | `CSweet.Office.BuilderGuest` | `/usr/lib/csweet/builder/` |
| `1` Runtime | the materialized `payload/<entrypoint>` | `/run/csweet/artifact/payload` |
| `2` ToolchainBuild | `CSweet.Office.ToolchainGuest` | `/usr/lib/csweet/toolchain/` |

All three are published into the image as root-owned `0755` single-file executables, alongside
`/usr/lib/csweet/guest/CSweet.Office.RuntimeGuest` — the broker that is always present and always the first
process to run.

`GuestWorkloadSupervisor.ResolveExecutable` enforces that a workload executable stays inside the read-only
artifact root, with **one** hard-coded exception: kind 2 may target
`/usr/lib/csweet/toolchain/CSweet.Office.ToolchainGuest`.

## Device layout

Identical for Firecracker and Apple Virtualization; Hyper-V presents the artifact as optical media instead.

| Device | Role | Mode | Attached by |
|---|---|---|---|
| `/dev/vda` | Certified guest image (root) | read-only | all |
| `/dev/vdb` | Scratch | read-write, wiped and formatted at boot | all |
| `/dev/vdc` | Artifact media | read-only | Firecracker, Apple |
| `/dev/sr0` | Artifact media | read-only | Hyper-V |

`prepare-runtime.sh` validates the layout at every boot: `/dev/vdb` must exist and be writable (exit 3),
must not already be mounted (exit 4), and if `/dev/vdc` exists it must report read-only (exit 5).

## Guest hardening baked into the image

From `build/linux-firecracker/provision-guest.sh`:

- `csweet-workload` system user and group created with `/nonexistent` home and `nologin`.
- `/usr/lib/csweet/prepare-runtime.sh` written as the boot entry point.
- `csweet-vsock.service` loads `vsock` and `vmw_vsock_virtio_transport`; `csweet-agent-guest.service` pins
  `CSWEET_GUEST_BROKER_TRANSPORT=firecracker-vsock`, `CSWEET_GUEST_VSOCK_PORT=5000`, and
  `CSWEET_GUEST_ARTIFACT_DEVICE=/dev/vdc`, with `Restart=no`.
- `systemd-networkd` and `systemd-resolved` masked.
- `/etc/machine-id` blanked.
- `fstab` set to `/dev/vda / ext4 ro,nosuid,nodev` plus `tmpfs /tmp rw,nosuid,nodev,mode=1777`.
- `NoNewPrivileges=false` on the guest unit specifically, because the service must be able to drop to the
  workload user via `setpriv` — the drop itself is what enforces `--no-new-privs` on the workload.

## How images are built

### Windows / Hyper-V

`scripts/windows/New-CSweetHyperVTestGuest.ps1` publishes the three guest projects for `linux-x64` as
self-contained single-file executables and delegates to the shared image builder:

```powershell
Import-Module <IsolationRoot>\tools\LinuxImage\CSweet.LinuxImage.psd1
New-CSweetLinuxHyperVImage -ProfileDirectory build\windows-hyperv `
    -GuestServiceName csweet-agent-guest.service `
    -ArtifactDirectory <IsolationRoot>\artifacts\linux-images
```

> **Out of repo:** `CSweet.LinuxImage` lives in the sibling `CSweet.Isolation` repository. The script imports
> it as its first statement after the parameter block, so a missing sibling checkout fails immediately.

The Packer profile `build/windows-hyperv/csweet-agent-guest.pkr.hcl` requires `hashicorp/hyperv 1.1.5` and the
variables `iso_url`, `iso_checksum`, `switch_name`, `ssh_private_key_file`, `seed_iso_path`,
`guest_publish_directory`, `builder_publish_directory`, and `toolchain_publish_directory`.
`user-data.pkrtpl` installs `dotnet-sdk-10.0`, `linux-tools-virtual`, and
`linux-cloud-tools-common/virtual`, and requires `/usr/sbin/hv_kvp_daemon`.

Output lands in `artifacts/windows-runtime/source/` as `csweet-agent-guest-<sha256>.vhdx` with a `.ready`
marker (whose contents are the build fingerprint) and a `.sig` file.

### Linux / Firecracker

`scripts/linux/new-firecracker-guest.sh` builds `rootfs.ext4` (`mkfs.ext4 -q -F -L CSWEET_ROOT -d …`) from a
staged root, installs the three runners via `build/linux-firecracker/provision-guest.sh`, and locates the
kernel and initrd with `find "$rootfs/boot" -maxdepth 1 -type f -name 'vmlinuz-*' | sort -V | tail -n 1`.

### macOS

`scripts/macos/new-runtime-payload.sh` assembles the payload from a pre-built image plus the app-bundle
inputs. The Swift helper itself is built from
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Package.swift` (Swift tools 5.9, macOS 14) and requires
the `CSweet.AppleVirtualization.entitlements` entitlement.

## The bundle fingerprint

`Initialize-CSweetWindowsIsolationTest.ps1` computes a fingerprint so a matching guest image can be reused
instead of rebuilt:

```
SHA-256 over sorted "<relative path>\0<sha256>" records for every non-bin/obj file under
  src/CSweet.Office.RuntimeGuest
  src/CSweet.Office.BuilderGuest
  src/CSweet.Office.ToolchainGuest
  ../CSweet.Isolation/tools/LinuxImage
  src/CSweet.Office.Runtime.Protocol
  build/windows-hyperv
  scripts/windows/New-CSweetHyperVTestGuest.ps1
  Directory.Build.props, Directory.Packages.props, global.json (when present)
```

The cached image is reused only when a `csweet-agent-guest-<fingerprint>*.vhdx.ready` marker exists whose
**content equals** the fingerprint and whose `.vhdx` exists. `-RebuildGuest` forces a rebuild into a new
`<fingerprint>-<guid>.vhdx`.

> **Consequence for documentation and tooling changes:** adding any file to those five roots changes the
> fingerprint and forces a full guest rebuild. This is why
> [50-development/09-guest-image-changes.md](../50-development/09-guest-image-changes.md) tells you to put
> project READMEs elsewhere.

Note also what the fingerprint does **not** include: `RuntimeHost`, `Node`, `Configurator`, or
`HyperV.Helper`. Changing host code leaves the guest cache warm, but the payload must still be regenerated
before reinstalling.

## Certification binds the image

`CertifiedGuestImageRegistry` resolves a logical image id to a `GuestImageReference` from the **selected
provider's live certification**, not from control-plane input:

- `certifiedDigest = certification.GuestImageDigest`,
- `version = certification.CertificationSuiteVersion` when the request supplies none,
- and a hard failure with the user-facing *"installed secure agent runtime is out of date"* message when
  `RequiredCertificationSuiteVersion` differs.

The `PlatformIsolationBackendOptions` record holds the full binding: `GuestImagePath`, `GuestImageDigest`,
the detached `GuestImageSignaturePath`, the pinned `GuestImageSigningCertificatePath` and thumbprint, the
`CertificationEvidencePath` and digest, `CertifiedAt` and `CertificationExpiresAt`, `BrokerProtocolVersion`,
`HelperExecutablePath` and digest, `ArtifactImageRoot`, and the required `GuestChannelTransport`.

`ExternalPlatformIsolationBackend.ValidateWorkload` forces, on every create,
`workload.GuestImage.Digest == options.GuestImageDigest` **and**
`workload.BrokerLease.ExpectedGuestImageDigest == options.GuestImageDigest`.

## Practical consequences

| If you change | You must |
|---|---|
| Guest code (`RuntimeGuest`, `BuilderGuest`, `ToolchainGuest`) | Rebuild the guest image, then re-certify, then rebuild the payload. |
| Anything under `src/CSweet.Office.Runtime.Protocol` | Rebuild the guest image (it is part of the fingerprint). |
| `RuntimeHost`, `Node`, `Configurator`, or a helper | Rebuild the payload. The guest image cache stays valid. |
| Provider configuration or the broker protocol version | Re-certify. |

## Sources

`build/linux-firecracker/provision-guest.sh`, `build/windows-hyperv/{csweet-agent-guest.pkr.hcl,provision-guest.sh,user-data.pkrtpl}`,
`scripts/linux/new-firecracker-guest.sh`, `scripts/windows/{New-CSweetHyperVTestGuest.ps1,Initialize-CSweetWindowsIsolationTest.ps1,New-CSweetWindowsRuntimePayload.ps1}`,
`scripts/macos/new-runtime-payload.sh`, `src/CSweet.Office.Runtime.Core/{CertifiedGuestImageRegistry.cs,ExternalPlatformIsolationBackend.cs}`,
`src/CSweet.Office.RuntimeGuest/GuestWorkloadSupervisor.cs`.

Verified: 2026-09-15.
