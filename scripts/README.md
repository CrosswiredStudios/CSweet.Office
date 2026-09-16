# scripts

Deployment, release, and developer tooling for C-Sweet Office. Nothing here is compiled into the product; these scripts install, package, certify, and remove it.

Part of the C-Sweet Office repository. Full parameter reference for every script: [`docs/80-reference/scripts.md`](../docs/80-reference/scripts.md).

## Folders

| Folder | Purpose |
|---|---|
| `scripts/` | Repository-level end-to-end exercise against a running C-Sweet control plane. |
| `scripts/windows/` | Windows installer, uninstaller, MSI build, payload generator, Hyper-V guest image, recovery, and diagnosis. |
| `scripts/linux/` | Ubuntu installer, configurator, uninstaller, payload and package builders, Firecracker guest image, and the systemd unit templates. |
| `scripts/macos/` | macOS installer, configurator, uninstaller, payload and `.pkg` builders, and the launchd plist templates. |
| `scripts/release/` | Version-truth check, per-platform release entry points, release manifest writer, and release verification. |
| `scripts/tests/` | The PowerShell harness behind a Windows-only xUnit test. |

## `scripts/`

| Script | Purpose |
|---|---|
| `office-e2e.ps1` | End-to-end exercise of the C-Sweet API for an imported agent: health check, import preview, install, scheduled run, artifact check, cleanup. No Office service is contacted. |

## `scripts/windows/`

| Script | Purpose |
|---|---|
| `Initialize-CSweetWindowsIsolationTest.ps1` | Developer bootstrap: caches the Hyper-V guest image by fingerprint, publishes helper, probe, and smoke test, runs the isolation smoke test, and writes a certified payload. |
| `New-CSweetWindowsRuntimePayload.ps1` | Publishes RuntimeHost, the Hyper-V helper, the Node, and the Configurator, and writes the Windows payload manifest. |
| `New-CSweetOfficeMsi.ps1` | Builds and signs the WiX MSI; registers the `csweet-office` URL protocol and the `CSWEET_FORCE_REMOVE` property. |
| `Install-CSweetOffice.ps1` | Unprivileged entry point: self-elevates and forwards every argument to `Install-CSweetOfficeRuntimeHost.ps1`. |
| `Install-CSweetOfficeRuntimeHost.ps1` | The Windows installer: payload verification, versioned install, ACLs, `appsettings.json`, services, trust pre-pin, and enrollment. |
| `Uninstall-CSweetOffice.ps1` | Removes both services, the maintenance service, shell integration, package files, group membership, and owned Hyper-V resources. |
| `Remove-CSweetOfficeForRecovery.ps1` | Staged, explicitly authorized removal used by the recovery flow; reports completion to Headquarters with a pinned certificate check. |
| `Repair-CSweetOfficeRuntimeHostAccess.ps1` | Re-applies package ACLs, the guest-image grant, and the RuntimeHost Hyper-V group membership, and rewrites access configuration. |
| `Enter-CSweetOfficeMaintenance.ps1` | Two-gate maintenance entry: verifies the office id, stops the Node, rechecks the recovery state, then writes the drain marker. |
| `Get-CSweetOfficeRecoveryState.ps1` | Prints exactly one of `none`, `clean`, `active`, or `unsafe` for the upgrade, repair, and reconnect gates. |
| `Diagnose-CSweetOfficeRuntimeHostStart.ps1` | Collects a Process Monitor trace of a RuntimeHost start attempt for access-denied diagnosis. |
| `Clear-CSweetGeneratedHyperVImages.ps1` | Removes generated Hyper-V images and their VM registrations, writing an inventory to the cleanup log. |
| `New-CSweetHyperVTestGuest.ps1` | Builds the Ubuntu guest VHDX with Packer through the shared `CSweet.LinuxImage` module. |
| `New-CSweetNoCloudSeedIso.ps1` | Builds a NoCloud cloud-init seed ISO from a `meta-data` and `user-data` directory. |
| `CSweet.WindowsSetupProgress.ps1` | Dot-sourced setup-progress helper module; not executed directly. |

## `scripts/linux/`

| Script | Purpose |
|---|---|
| `install-office.sh` | Installs the payload, creates service identities, seeds the shared key, initializes assignment trust, and starts both services. |
| `configure-office.sh` | Root-owned configurator: prints the security label and its implications, then execs the installer. |
| `uninstall-office.sh` | Removes units, configuration, payload, state, media, and the service identities. |
| `new-runtime-payload.sh` | Builds the certified Linux payload and writes `runtime-manifest.json`. |
| `new-native-packages.sh` | Builds `.deb` and/or `.rpm` packages from a payload, embedding the configurator. |
| `new-firecracker-guest.sh` | Builds the Firecracker guest `rootfs.ext4` from a debootstrapped Ubuntu root and locates the kernel and initrd. |
| `initialize-firecracker-test.sh` | Development loop: builds a guest, produces a payload, installs Office, and runs the smoke test. |

`csweet-office-node.service` and `csweet-office-runtime.service` are systemd unit templates, not scripts.

## `scripts/macos/`

| Script | Purpose |
|---|---|
| `install-office.sh` | Installs the verified, signed payload, creates the service identities, seeds the key, and bootstraps both launchd jobs. |
| `configure-office.sh` | Root-owned configurator: validates the HTTPS URL and execs the installer from the installed payload. |
| `uninstall-office.sh` | Boots out both jobs and removes plists, application-support trees, identities, configurators, and the package receipt. |
| `new-runtime-payload.sh` | Builds the macOS payload: publishes the services, builds and signs the Swift helper, and writes the manifest. |
| `new-installer-package.sh` | Builds a signed, notarized `.pkg`, verifying signatures and the helper entitlement first. |

`com.csweet.office.node.plist` and `com.csweet.office.runtime.plist` are launchd templates, not scripts.

## `scripts/release/`

| Script | Purpose |
|---|---|
| `Get-OfficeReleaseMetadata.ps1` | The single version-truth check: the tag must equal `VersionPrefix`, and the contracts pin must be one `MAJOR.MINOR.PATCH`. |
| `Invoke-PlatformRelease.ps1` | Per-platform release entry point: independent-solution tests, symbol archive, payload, and installer. |
| `Invoke-LinuxRelease.ps1` | Builds the Linux payload and a signed `.deb` only. |
| `Invoke-MacRelease.ps1` | Builds the macOS payload and the signed, notarized `.pkg`. |
| `New-OfficeReleaseManifest.ps1` | Writes `office-release.json`, describing every installer asset. |
| `verify-release.sh` | Verifies a release directory: checksums, manifest JSON, SBOM presence, and the asset listing. |

## `scripts/tests/`

| Script | Purpose |
|---|---|
| `Test-OfficeUpgradeProbe.ps1` | Harness for `Get-CSweetOfficeRecoveryState.ps1`: stubs the host, builds a fake install tree, and asserts the eight state transitions. |

## Notes

- The installer, uninstaller, repair, and maintenance scripts require administrator (Windows, self-elevating) or root (Linux, macOS). The Windows installer refuses to run on a domain controller (SEC-INV-17), and upgrade, uninstall, and reconnect paths check the drain gate.
- Several test classes assert on the **text** of these scripts (`LinuxInstallationTests`, most of `WindowsHyperVOnboardingTests`). Those tests pass against a present-but-broken script, so a green suite does not prove a script works — run the script you change.
- Release scripts run only on the hardened platform workflows; nothing here publishes or signs from a development machine.
- The guest image builds live in [`build/`](../build/README.md), not this directory.
