# Office installation storage

**Audience:** administrators choosing a destination for a fresh Office installation.

`CSweet.OfficeStorage.ps1` and `office-storage.sh` discover mounted fixed local volumes and
select the system volume by default. Interactive portable installers show the inventory before
enrollment. An explicit `-StorageVolumeId` (Windows) or `--storage-volume UUID` (Linux/macOS)
selects another eligible volume. Windows assisted setup accepts the authenticated Headquarters
selection; paths in the handoff URI are not used as installation authority.

> **Out of repo:** the onboarding selector and session persistence belong to Headquarters.
> `CSweet.Office.Configurator/Program.cs` consumes `AssistedOfficePreflightResponse.Storage`
> and `RedeemAssistedOfficeSetupResponse.Storage` from Office.Contracts 0.12.0.

## Destinations

On an alternative volume the active runtime uses `<mount>/CSweet/Office/program` for packages
and `<mount>/CSweet/Office/data` for mutable Office state, caches, and VM storage. Selecting the
system volume preserves the established platform paths documented in the installation pages.
Native package-manager payloads, registrations, launchers, and installer bookkeeping retain their
platform-defined locations. The Windows assisted launcher stages large bundles and certification
under the chosen Office data directory; its small progress/secret files retain their protected
ProgramData location.

Windows supports NTFS/ReFS fixed volumes; Linux discovery offers ext4, XFS, and Btrfs local block
filesystems; macOS discovery offers internal APFS/HFS volumes with writable service storage.
Removable/network-backed storage, read-only/no-execution mounts, unsafe paths, and insufficient
space are refused by the installer. The installer rechecks the selected destination; it does not
silently switch to another disk when a selection becomes unavailable.

## Maintenance and removal

The location record is administrator-owned: `HKLM\SOFTWARE\CSweet\Office\Storage` on Windows,
`/etc/csweet/office-storage` on Linux, and
`/Library/Preferences/com.csweet.office.storage` on macOS. Unix records contain data, not sourced
shell code. `Resolve-CSweetOfficeStorage` and `office_load_storage` validate the record against
the mounted volume and the installer-owned directory layout before maintenance or removal.
Windows removal retains its registration until directory cleanup finishes, allowing retries.
Legacy installations without a record keep their existing system destination. Moving an installed
Office is not supported by this feature.

`OfficeStorageGuard.VerifyWindowsVolume` checks the registered volume before either Windows
service starts. Linux's units verify storage before startup and declare mount
dependencies; macOS launchd invokes `office-start.sh` to verify storage before launching either
service. Missing or replaced storage leaves the service unavailable. SEC-INV-16 and SEC-INV-20
continue to apply.

## Verification

`Test-OfficeStorage.ps1` executes the Windows resolver using controlled volume inventory.
`test-office-storage.sh` executes Unix selection, registration, and replacement-volume rejection.
Headquarters' `Test-AutomaticOfficeUpdatePreparation.ps1` includes an alternative-drive preparation
case and checks installed-helper parity. Native install/reboot/workload/update/uninstall acceptance
on a second fixed volume remains required on each platform before release certification.

## Sources

`scripts/windows/{CSweet.OfficeStorage,Install-CSweetOffice,Install-CSweetOfficeRuntimeHost,
Get-CSweetOfficeRecoveryState,Enter-CSweetOfficeMaintenance,Repair-CSweetOfficeRuntimeHostAccess,
Uninstall-CSweetOffice,Start-CSweetOfficeAutomaticUpdate}.ps1`,
`src/CSweet.Office.Configurator/Program.cs`, `src/CSweet.Office.Runtime.Core/OfficeStorageGuard.cs`,
`src/CSweet.Office.{Node,RuntimeHost}/Program.cs`, `scripts/{office-storage,office-start}.sh`,
`scripts/{linux,macos}/{install-office,configure-office,uninstall-office,new-runtime-payload}.sh`,
`scripts/linux/csweet-office-{runtime,node}.service`, `scripts/macos/com.csweet.office.{node,runtime}.plist`,
`scripts/tests/{Test-OfficeStorage.ps1,test-office-storage.sh}`.

Verified: 2026-10-24.
