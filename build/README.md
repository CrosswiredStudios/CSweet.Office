# build

Guest image build inputs. Two profiles produce the Ubuntu guests that the isolation providers boot: `windows-hyperv` for the Hyper-V Generation 2 `.vhdx` and `linux-firecracker` for the Firecracker root filesystem. Both install the three published guest executables — `CSweet.Office.RuntimeGuest`, `CSweet.Office.BuilderGuest`, and `CSweet.Office.ToolchainGuest`.

Part of the C-Sweet Office repository. Full documentation: [`docs/30-workloads/06-guest-images.md`](../docs/30-workloads/06-guest-images.md).

## `windows-hyperv/`

| File | Role |
|---|---|
| `csweet-agent-guest.pkr.hcl` | Packer template using the `hyperv-iso` source: generation 2, secure boot with the `MicrosoftUEFICertificateAuthority` template, Ubuntu autoinstall, and file provisioners for the three guest executables. |
| `provision-guest.sh` | Installs the executables under `/usr/lib/csweet/{guest,builder,toolchain}`, creates the `csweet-workload` service user, and writes `/usr/lib/csweet/prepare-runtime.sh`, which mounts the disposable scratch disk and execs the runtime guest broker. |
| `user-data.pkrtpl` | The cloud-init autoinstall seed: identity `csweet-image`, direct storage layout, and the `dotnet-sdk-10.0` and Hyper-V guest tooling packages. |

`scripts/windows/New-CSweetHyperVTestGuest.ps1` drives Packer and writes `artifacts/windows-runtime/source/csweet-agent-guest-<fingerprint>.vhdx` plus its `.ready` and `.sig` companions.

## `linux-firecracker/`

| File | Role |
|---|---|
| `provision-guest.sh` | Provisions the guest root filesystem: the same three executables, the `csweet-workload` user, `prepare-runtime.sh` for the fixed scratch (`/dev/vdb`) and artifact (`/dev/vdc`) devices, and the `csweet-vsock.service` and `csweet-agent-guest.service` units. |

`scripts/linux/new-firecracker-guest.sh` builds the root filesystem from a debootstrapped Ubuntu root and writes `rootfs.ext4` plus the kernel and initrd.

## Sibling dependency

Both profiles depend on a sibling `../CSweet.Isolation` checkout. `New-CSweetHyperVTestGuest.ps1` imports its `tools/LinuxImage/CSweet.LinuxImage.psd1` module, and the Windows build fingerprint hashes `tools/LinuxImage` — a missing checkout fails the fingerprint outright.

## Warning

> **Nothing may be added under `build/windows-hyperv/`.** `Get-GuestBuildFingerprint` in `scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1` hashes every non-`bin`/`obj` file under it, so adding a file changes the fingerprint, invalidates the cached guest image, and forces a full Packer rebuild. The same rule and the full fingerprint list are in [`docs/50-development/09-guest-image-changes.md`](../docs/50-development/09-guest-image-changes.md).

Documentation for the guest images belongs in [`docs/`](../docs/README.md), not in this directory. The Linux guest is rebuilt on every `initialize-firecracker-test.sh` run; only the Windows path caches by fingerprint.
