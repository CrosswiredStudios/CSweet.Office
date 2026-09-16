# File layout

**Audience:** operators writing a firewall rule, an ACL, a backup policy, or a cleanup script; contributors
adding a file to the payload.

Every path below is created by an installer or by a service at runtime. Paths marked *state* change during
operation and must be protected; paths marked *package* are immutable and must not be writable by a service
identity (`SEC-INV-16`).

## Windows

### Install tree — `%ProgramFiles%\CSweet\Office`

| Path | Kind | Access |
|---|---|---|
| `<version>\` | package | `(OI)(CI)RX` for the Node and RuntimeHost service SIDs; `(OI)(CI)F` for `SYSTEM` and `Administrators`; inheritance removed |
| `<version>\runtime\CSweet.Office.RuntimeHost.exe` | package | as above; `Assert-FileReadExecuteAce` must pass for the RuntimeHost SID |
| `<version>\node\CSweet.Office.Node.exe` | package | as above; `Assert-FileReadExecuteAce` must pass for the Node SID |
| `<version>\helper\CSweet.Office.Runtime.HyperV.Helper.exe` | package | as above |
| `<version>\configurator\CSweet.Office.Configurator.exe` | package | as above; also the maintenance-service executable |
| `<version>\images\csweet-agent-guest.vhdx` and `.sig` | package | as above, plus `S-1-5-83-0` (Virtual Machines) `R` on the file and `RX` on its directory — never write, never inherited |
| `<version>\certificates\guest-image-signing.cer` | package | as above |
| `<version>\certification\windows-hyperv.json` | package | as above |
| `<version>\runtime-manifest.json` | package | as above |
| `<version>\appsettings.json` | configuration | as above; rewritten by every install |
| `recovery\<version>\` | package | pristine copy of the verified payload, used by the repair path; same ACL treatment |
| `maintenance-settings.json` | configuration | `SYSTEM:F` and `Administrators:F` only (`SEC-INV-19`) |
| `Install-CSweetOffice.ps1`, `Install-CSweetOfficeRuntimeHost.ps1`, `CSweet.WindowsSetupProgress.ps1`, `Get-CSweetOfficeRecoveryState.ps1`, `Enter-CSweetOfficeMaintenance.ps1`, `Remove-CSweetOfficeForRecovery.ps1`, `Uninstall-CSweetOffice.ps1` | script | staged flat in the install root by the MSI and by the RuntimeHost installer |

### Data tree — `%ProgramData%\CSweet\Office`

| Path | Kind | Access |
|---|---|---|
| `artifacts\` | state | Node `(OI)(CI)M`; RuntimeHost `(OI)(CI)R`; `SYSTEM`/`Administrators` `(OI)(CI)F` |
| `artifact-media\` | state | Node `(OI)(CI)M`; RuntimeHost `(OI)(CI)R`; `SYSTEM`/`Administrators` `(OI)(CI)F` |
| `node\` | state | Node `(OI)(CI)M`; `SYSTEM`/`Administrators` `(OI)(CI)F` |
| `authorization\` | state | RuntimeHost `(OI)(CI)M`; `SYSTEM`/`Administrators` `(OI)(CI)F` |
| `hyperv\` | state | RuntimeHost `(OI)(CI)M`; `SYSTEM`/`Administrators` `(OI)(CI)F` |
| `runtime-host.key` | secret | interactive control-plane user `R`, Node `R`, RuntimeHost `R`, `SYSTEM:F`, `Administrators:F`; 32 random bytes, base64, single line |
| `node\node-state.json` | state | inside `node\` |
| `node\control-plane-trust.json` | state | Node `R`, `SYSTEM:F`, `Administrators:F` |
| `node\enrollment.secret` | secret | inside `node\`; deleted by the Node after the identity state is saved |
| `node\artifact-cache\` | state | inside `node\` |
| `node\maintenance\drain-state`, `node\maintenance\active-assignments\*.active` | state | inside `node\` |
| `authorization\headquarters-trust.json`, `authorization\accepted-assignments.json`, `authorization\authorized-workload-handles.json` | state | inside `authorization\` |

Every directory above is applied with `icacls /inheritance:r` so no parent grant leaks in.

### Other Windows locations

| Path | Owner | Purpose |
|---|---|---|
| `%ProgramData%\CSweet\Setup\windows-isolation-<jobId:N>.json` | control-plane user `R`; `SYSTEM`/`Administrators` `F` | Setup progress, written by `CSweet.WindowsSetupProgress.ps1`. |
| `%ProgramData%\CSweet\Diagnostics\RuntimeHostStart\` | administrator | Default `OutputRoot` of `Diagnose-CSweetOfficeRuntimeHostStart.ps1`. |
| `%ProgramData%\CSweet\RuntimeHost\HyperV\` | RuntimeHost | Default `CSWEET_HYPERV_DATA_ROOT`; `instances\` and `vm-config\` are created beneath it and may not escape it. |
| `%ProgramData%\CSweet\Office\artifact-media` | — | Fallback artifact media root used by the Hyper-V helper when `CSWEET_ARTIFACT_MEDIA_ROOT` is unset. |
| `%ProgramData%\CSweet\AgentRuntime` | Headquarters | Legacy root. The first installer retires legacy VMs that own files inside it but never deletes the directory itself. |
| `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\00000ac9-facb-11e6-bd58-64006a7986d3` | installer | Broker vSock registration, `ElementName` *"C-Sweet authenticated agent broker"*; the braced legacy key is removed. |
| `HKLM\SYSTEM\CurrentControlSet\Services\CSweet.Office.RuntimeHost\Environment` | installer | `MultiString` with the three machine-scope `CSWEET_*` variables. |

## Linux

| Path | Kind | Owner and mode |
|---|---|---|
| `/opt/csweet/office/` | package | `root:root`; executables `0755`, `firecracker/vmlinux` and `initrd.img` `0644`, `runtime-manifest.json` `0644` |
| `/opt/csweet/office/CSweet.Office.{RuntimeHost,Node}` | package | `root:root 0755` |
| `/opt/csweet/office/CSweet.Office.Runtime.Firecracker.Helper` | package | `root:root 0755` |
| `/opt/csweet/office/firecracker/{firecracker,jailer}` | package | `root:root 0755` |
| `/usr/lib/csweet/office-installer/` | package | full payload tree, used by `configure-office.sh` |
| `/etc/csweet/office.env` | configuration | `0600` |
| `/etc/csweet/runtime-host.env` | configuration | `0600` |
| `/etc/systemd/system/csweet-office-{node,runtime}.service` | configuration | `0644` |
| `/usr/sbin/csweet-configure-office`, `/usr/sbin/csweet-uninstall-office` | script | installed by the native package |
| `/var/lib/csweet/office/` | state | `0755` |
| `/var/lib/csweet/office/node/` | state | `csweet-node:csweet-node 0700` |
| `/var/lib/csweet/office/node/{node-state.json,artifact-cache,maintenance,enrollment.secret}` | state/secret | inside the node directory |
| `/var/lib/csweet/office/authorization/` | state | `root:root 0700`; `headquarters-trust.json` `0600` |
| `/var/lib/csweet/office/firecracker/` | state | `root:root 0700` |
| `/var/lib/csweet/office/runtime-host.key` | secret | `root:csweet-runtime 0640`; 32 random bytes, base64 |
| `/var/lib/csweet/artifact-media/` | state | `csweet-node:csweet-runtime 0770` |
| `/var/lib/csweet/setup/local-provisioning-<jobId>.result` | state | `root:root 0644`, written when `--result-job-id` is supplied |
| `/run/csweet/` | runtime | created by systemd (`RuntimeDirectory=csweet`, mode `0770`) |
| `/run/csweet/csweet-office-runtime-v1.sock` | runtime | `0660`; removed on shutdown |

System identities created by the installer: `csweet-node` (home `/var/lib/csweet/office`, shell
`/usr/sbin/nologin`), `csweet-vm` (home `/nonexistent`), and the group `csweet-runtime` with `csweet-node`
added to it.

## macOS

| Path | Kind | Owner and mode |
|---|---|---|
| `/Library/Application Support/CSweet/Office/` | package | `root:wheel 0755` |
| `/Library/Application Support/CSweet/Office/CSweet.Office.{RuntimeHost,Node}` | package | `root:wheel 0755` |
| `/Library/Application Support/CSweet/Office/CSweet.Office.Runtime.AppleVirtualization.Helper` | package | `root:wheel 0755`, must carry the `com.apple.security.virtualization` entitlement |
| `/Library/Application Support/CSweet/Office/runtime-manifest.json` | package | `root:wheel 0644` |
| `/Library/Application Support/CSweet/Office/apple-virtualization/` | package | guest kernel and helper package inputs |
| `/Library/Application Support/CSweet/Office/InstallerPayload/` | package | payload tree used by `configure-office.sh` |
| `/Library/Application Support/CSweet/Office/node/` | state | `_csweetnode:_csweet 0700` (enrollment token, node state, artifact cache, maintenance markers) |
| `/Library/Application Support/CSweet/Office/authorization/` | state | `root:wheel 0700`; `headquarters-trust.json` `0600` |
| `/Library/Application Support/CSweet/Office/artifact-media/` | state | `_csweetnode:_csweet 0770` |
| `/Library/Application Support/CSweet/Office/AppleVirtualization/` and `…/instances/` | state | `root:wheel 0700` |
| `/Library/Application Support/CSweet/Office/runtime-host.key` | secret | `root:_csweet 0640` |
| `/Library/Application Support/CSweet/Setup/local-provisioning-<jobId>.result` | state | `root:wheel 0644` |
| `/Library/LaunchDaemons/com.csweet.office.node.plist` | configuration | `0644`; job label `com.csweet.office` running as `_csweetnode` |
| `/Library/LaunchDaemons/com.csweet.office.runtime.plist` | configuration | `0644`; job label `com.csweet.office.runtime` running as root |
| `/var/run/csweet-av/` | runtime | `root:wheel 0700`; one manager socket per live instance, `csweet-av/<32 hex>.sock` |
| `/usr/local/sbin/csweet-configure-office`, `/usr/local/sbin/csweet-uninstall-office` | script | removed by the uninstaller |

The installer creates the group `_csweet` and the user `_csweetnode` (shell `/usr/bin/false`, home
`/var/empty`, hidden) with IDs allocated downward from 498.

## Payload layout

`New-CSweetWindowsRuntimePayload.ps1`, `scripts/linux/new-runtime-payload.sh`, and
`scripts/macos/new-runtime-payload.sh` produce the same shape. The Windows payload directory is the input to
the MSI; the Linux and macOS payloads are the package root that the native package and `.pkg` stage.

| Path | Contents |
|---|---|
| `runtime/` | Self-contained `CSweet.Office.RuntimeHost` publish output. |
| `helper/` | Self-contained platform helper: `CSweet.Office.Runtime.HyperV.Helper.exe` (Windows), `CSweet.Office.Runtime.Firecracker.Helper` (Linux), `CSweet.Office.Runtime.AppleVirtualization.Helper` (macOS). |
| `node/` | Self-contained `CSweet.Office.Node` publish output. |
| `configurator/` | Self-contained `CSweet.Office.Configurator` publish output; also hosts the maintenance service. |
| `images/` | `csweet-agent-guest.vhdx` and `.sig` (Windows); root filesystem, kernel, and initrd files (Linux, macOS). |
| `certificates/` | `guest-image-signing.cer`. |
| `certification/` | `windows-hyperv.json` (Windows); the provider's evidence file elsewhere. |
| `runtime-manifest.json` | Package manifest, written last. |
| `appsettings.json` | Development configuration; overwritten at the installed version root by the Windows installer. |
| `firecracker/` | Linux only: `firecracker`, `jailer`, `vmlinux`, `initrd.img`. |
| `apple-virtualization/` | macOS only: `vmlinux` guest kernel. |

`runtime-manifest.json` keys, exactly as written: `schemaVersion`, `packageVersion`, `officeVersion`,
`runtimeHostExecutable`, `helperExecutable`, `officeExecutable`, `configuratorExecutable`, `guestImage`,
`guestImageDigest`, `guestImageSignature`, `guestImageSigningCertificate`,
`guestImageSigningCertificateThumbprint`, `certificationSuiteVersion`, `certificationEvidence`,
`certificationEvidenceDigest`, `certifiedAt`, `certificationExpiresAt`, and `files` (one
`{ path, sha256 }` record per file, paths using `/`). `configuratorExecutable` is written but read by no
consumer; the maintenance service resolves its executable as `recovery\<version>\configurator\CSweet.Office.Configurator.exe`.

## Guest image layout

Inside the certified guest image, for all three workload kinds:

| Path | Kind | Owner and mode |
|---|---|---|
| `/usr/lib/csweet/guest/CSweet.Office.RuntimeGuest` | package | `root:root 0755` — the broker, always the first process |
| `/usr/lib/csweet/builder/CSweet.Office.BuilderGuest` | package | `root:root 0755` |
| `/usr/lib/csweet/toolchain/CSweet.Office.ToolchainGuest` | package | `root:root 0755` |
| `/usr/lib/csweet/prepare-runtime.sh` | package | `root:root 0755` — `ExecStart` of `csweet-agent-guest.service` |
| `/run/csweet/` | runtime | scratch filesystem (`/dev/vdb`, `ext4`, label `CSWEET_SCRATCH`) |
| `/run/csweet/artifact/` | runtime | materialized read-only artifact media |
| `/run/csweet/artifact/payload` | runtime | fixed runtime artifact root for kinds 1 and 2 |
| `/run/csweet/broker.sock` | runtime | `0660`, group `csweet-workload`, backlog 16 |
| `/run/csweet/workload-token` | runtime | `0640`, group `csweet-workload`; contains the boot token |
| `/opt/csweet/artifact/payload` | package | default artifact root when `CSWEET_GUEST_ARTIFACT_ROOT` is unset |

Device layout is identical for Firecracker and Apple Virtualization; Hyper-V attaches the artifact as optical
media:

| Device | Role | Mode | Providers |
|---|---|---|---|
| `/dev/vda` | certified guest image (root) | read-only | all |
| `/dev/vdb` | scratch | read-write, wiped and formatted at boot | all |
| `/dev/vdc` | artifact media | read-only | Firecracker, Apple Virtualization |
| `/dev/sr0` | artifact media | read-only | Hyper-V |

`CSWEET_GUEST_ARTIFACT_DEVICE` may only name `/dev/sr0` or `/dev/vdc`; any other value throws
`InvalidDataException`. `prepare-runtime.sh` fails the boot with exit `3` if `/dev/vdb` is missing or not
writable, `4` if it is already mounted, and `5` if an existing `/dev/vdc` does not report read-only.

## Artifact cache and media naming

| Item | Path | Rule |
|---|---|---|
| Cache entry | `<ArtifactCacheDirectory>\<digest[7..]>.artifact` | The `sha256:` prefix is stripped; the remaining 64 hex characters are the file name. |
| In-flight download | `<ArtifactCacheDirectory>\.<guid:N>.download` | Same directory, hidden prefix, `FileShare.None`; deleted on failure, moved over the cache entry on success. |
| Media image | `<ArtifactMediaDirectory>\<digest[7..]>.iso` | Single-file ISO 9660 written by `SingleFileIso9660.WriteAsync`; verified with `VerifyArtifactDigestAsync`. |
| Hyper-V attachment | `<instance>\artifact.iso` | The helper copies the verified ISO into the per-VM instance directory with `File.Copy(..., overwrite: false)` and re-verifies the digest there before attaching. |
| Windows guest image build output | `artifacts\windows-runtime\source\csweet-agent-guest-<sha256>.vhdx` plus `.ready` (build fingerprint) and `.sig` | `.ready` content must equal the fingerprint for the cached image to be reused. |
| Payload guest image | `images\csweet-agent-guest.vhdx` | Copied from the build output; its digest becomes `guestImageDigest` in the manifest. |

> **Same directory, two settings.** `CSweet:Office:Node:ArtifactMediaDirectory` belongs to the Node (it
> writes the ISO) and `CSweet:Office:Providers:<provider>:ArtifactImageRoot` belongs to the RuntimeHost (it
> attaches the ISO read-only). Both must name the same directory: on Windows the installer sets both to
> `%ProgramData%\CSweet\Office\artifact-media`, on Linux to `/var/lib/csweet/artifact-media`, and on macOS to
> `/Library/Application Support/CSweet/Office/artifact-media`. Pointing them at different directories fails
> closed: the Node writes media the RuntimeHost cannot read, and every runtime workload fails at
> `invalid-artifact-media`. The ACL on that directory is asymmetric on purpose — Node `(OI)(CI)M`,
> RuntimeHost `(OI)(CI)R`.

## Sources

`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1` (payload verification, ACL block, registry block, configuration block),
`scripts/windows/{New-CSweetWindowsRuntimePayload.ps1,New-CSweetOfficeMsi.ps1,CSweet.WindowsSetupProgress.ps1,Get-CSweetOfficeRecoveryState.ps1,Enter-CSweetOfficeMaintenance.ps1,Uninstall-CSweetOffice.ps1,Clear-CSweetGeneratedHyperVImages.ps1}`,
`scripts/linux/{install-office.sh,uninstall-office.sh,new-native-packages.sh,csweet-office-runtime.service,csweet-office-node.service}`,
`scripts/macos/{install-office.sh,uninstall-office.sh,com.csweet.office.node.plist,com.csweet.office.runtime.plist,new-installer-package.sh}`,
`build/linux-firecracker/provision-guest.sh`,
`src/CSweet.Office.Node/{OfficeArtifactCache.cs,OfficeOptions.cs}`,
`src/CSweet.Office.Runtime.Artifacts/{ArtifactStoreOptions.cs,ArtifactMediaOptions.cs,FileSystemAgentArtifactMediaStore.cs}`,
`src/CSweet.Office.Runtime.Core/SingleFileIso9660.cs`,
`src/CSweet.Office.Runtime.HyperV.Helper/{HyperVHelperPaths.cs,HyperVHelperController.cs}`,
`src/CSweet.Office.RuntimeGuest/{GuestArtifactMaterializer.cs,GuestServiceOptions.cs,GuestWorkloadSupervisor.cs}`,
`docs/10-system/06-process-network-and-storage-surface.md`, `docs/30-workloads/06-guest-images.md`.

Verified: 2026-09-15.
