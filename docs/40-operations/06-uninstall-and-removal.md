# Uninstall and removal

**Audience:** administrators removing an Office permanently, and reviewers checking what removal actually does.

Removal is destructive and gated: every platform refuses to proceed until the Office is drained with zero active
assignments, and every platform offers a `--force` / `-Force` escape that is intended for one situation only —
an Office that has already been revoked in C-Sweet and whose work may be destroyed.

## Windows

### The uninstall script

```
.\Uninstall-CSweetOffice.ps1 [-Force] [-ProgressPath <path>] [-ProgressJobId <guid>] [-ProgressWorkflow <label>]
```

The script self-elevates with `runas` and re-enters itself with `-Elevated`. It detects an installation from any
of these: one of the five service names it knows, `%ProgramFiles%\CSweet\Office`, `%ProgramData%\CSweet\Office`,
or the legacy `SatelliteOffice` roots.

| Gate | Behaviour |
|---|---|
| Installation detected, drain state is not `draining`, or one or more `*.active` markers exist | Throws: *"Drain this node in C-Sweet and wait for active assignments to reach zero before uninstalling. Use -Force only after revocation."* |
| `-Force` | Skips the gate. |
| Progress wiring | Only written when both `-ProgressPath` and `-ProgressJobId` are supplied. |

### Windows Installer

The MSI runs the same uninstaller as a deferred, non-impersonated custom action before `RemoveFiles`, when the
action is a full removal of a product that is not being upgraded:

| Condition | Custom action |
|---|---|
| `REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE AND CSWEET_FORCE_REMOVE<>"1"` | `RemoveExecutionFleet` → `Uninstall-CSweetOffice.ps1 -Elevated` |
| `REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE AND CSWEET_FORCE_REMOVE="1"` | `ForceRemoveExecutionFleet` → `Uninstall-CSweetOffice.ps1 -Force -Elevated` |

`CSWEET_FORCE_REMOVE` is a secure property, which is why a guided removal can pass it through `msiexec`
without it being a user-settable flag in normal use.

### The recovery removal helper

`Remove-CSweetOfficeForRecovery.ps1` is the only path that removes an Office without an interactive prompt. It
requires all of the following parameters and is documented in
[05-repair-and-recovery.md](05-repair-and-recovery.md):

| Parameter | Purpose |
|---|---|
| `-ParentProcessId` | The configurator waits for this process to exit **before** any teardown. |
| `-UninstallScript` | The staged copy of `Uninstall-CSweetOffice.ps1`, used when no product code is registered. |
| `-ControlPlaneOrigin` | Absolute HTTPS origin, or loopback HTTP. Every request is confined to this origin. |
| `-HandoffSecretPath`, `-SetupReceiptPath` | Protected removal authorization, read once and deleted in `finally`. |
| `-AssistedSetupSessionId` | Correlation with the C-Sweet session. |
| `-ControlPlaneCertificateSha256` | Optional pin; when present, certificate validation is replaced by a fixed-time comparison of the leaf's SHA-256. |

It removes the product through `msiexec /x <ProductCode> /qn /norestart CSWEET_FORCE_REMOVE=1` (accepting exit
codes 0, 1605, and 1614) or through the staged script with `-Force -Elevated`, then reports to Headquarters. See
`SEC-INV-19` for what the service that stages this helper is not allowed to do.

### Removed on Windows

| Item | Detail |
|---|---|
| Services | `CSweet.Office.Maintenance`, `CSweet.Office.Node`, `CSweet.Office.RuntimeHost`, and the legacy `CSweet.SatelliteOffice.Node` / `CSweet.SatelliteOffice.RuntimeHost`, all stopped first and deleted with `sc.exe delete` (exit 1060 is tolerated). |
| Owned VMs | Any Hyper-V VM whose configuration, snapshot, smart-paging, or hard-disk path lies under the Office data root or the legacy Hyper-V root is turned off and removed, and attached `*.vhd`/`*.vhdx` files under those roots are dismounted. |
| Hyper-V privilege | RuntimeHost service SIDs are removed from `Hyper-V Administrators`. |
| Registry | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\00000ac9-facb-11e6-bd58-64006a7986d3`, `HKLM\Software\Classes\csweet-office`, and `HKLM\Software\CSweet\Office`. |
| Machine environment | `CSWEET_HYPERV_BROKER_SERVICE_ID`, `CSWEET_HYPERV_DATA_ROOT`, `CSWEET_ARTIFACT_MEDIA_ROOT` are cleared. |
| Directories | `%ProgramFiles%\CSweet\Office` (all version roots, the pristine `recovery\<version>` copies, the maintenance settings, and the installer scripts), `%ProgramData%\CSweet\Office` (identity, Pfx, enrollment secret, authorization ledger and trust pin, artifact cache, artifact media, Hyper-V instances, and `runtime-host.key`), and the legacy `%ProgramFiles%\CSweet\SatelliteOffice` and `%ProgramData%\CSweet\SatelliteOffice` roots. |

Removal of a directory is retried up to ten times on `IOException` or `UnauthorizedAccessException` and then
**fails the uninstall** if the path still exists, so a partial removal is reported rather than silently
accepted.

### Deliberately not removed on Windows

| Item | Why |
|---|---|
| `%ProgramData%\CSweet\AgentRuntime` | The legacy Execution Node root and its immutable agent artifact store belong to Headquarters; the installer retires legacy VMs but keeps packages existing businesses may still need. |
| `%ProgramData%\CSweet\Setup` | Setup, repair, and removal progress files, including `windows-isolation-*.json`, are outside the Office data root and are left in place as an audit trail. |
| The Application Event Log sources `CSweet.Office.Node` and `CSweet.Office.RuntimeHost` | Registered once by the installer and not unregistered, because a source that disappears underneath a still-running log writer is worse than a stale one. |
| The enrollment record in C-Sweet | Revocation is a Headquarters action. The script's closing message says so explicitly: *"Revoke the host in fleet administration if it was not already revoked."* |
| Repository build artifacts | `artifacts\windows-runtime` and `artifacts\windows-test` are untouched; the closing message states that repository build artifacts were not removed. |

## Linux

```
sudo csweet-uninstall-office [--force]
```

Root is required, and the only accepted argument is `--force`. The gate is identical to the installer's upgrade
gate: it reads `/var/lib/csweet/office/maintenance/drain-state` and counts
`/var/lib/csweet/office/maintenance/active-assignments/*.active`, printing *"Drain this node in C-Sweet and wait
for active assignments to reach zero before uninstalling."* and exiting **3** when either check fails. The
`--force` message makes the intent explicit: *"Use --force only after the node is revoked and active workloads
may be terminated."*

Because the deb/rpm `prerm` script invokes this uninstaller, `apt remove csweet-office` is subject to the same
gate.

| Removed | Not removed |
|---|---|
| `csweet-office-node.service` and `csweet-office-runtime.service` (disabled, stopped, unit files deleted, systemd reloaded). | `/usr/lib/csweet/office-installer/` — package-owned, so `dpkg` removes it with the package. |
| `/etc/csweet/office.env` and `/etc/csweet/runtime-host.env`. | `/var/lib/csweet/setup/local-provisioning-*.result` — provisioning results written for `--result-job-id`. |
| `/opt/csweet/office`, `/var/lib/csweet/office` (identity, Pfx, authorization state, Firecracker instance state, `runtime-host.key`), `/var/lib/csweet/artifact-media`. | `/usr/sbin/csweet-configure-office` and `/usr/sbin/csweet-uninstall-office` — package-owned files that the package manager removes, not the uninstaller script. |
| `/opt/csweet`, `/etc/csweet`, and `/var/lib/csweet` when they are left empty. | The enrollment record in C-Sweet; revoke it in fleet administration if it was not already revoked. |
| Users `csweet-node` and `csweet-vm`, and the group `csweet-runtime`. | |

## macOS

```
sudo csweet-uninstall-office [--force]
```

Root is required. The gate reads
`/Library/Application Support/CSweet/Office/maintenance/{drain-state,active-assignments}` and exits **3** with the
same instruction when the Office is not drained with zero active assignments.

| Removed | Not removed |
|---|---|
| `com.csweet.office` and `com.csweet.office.runtime` (booted out), and `/Library/LaunchDaemons/com.csweet.office.node.plist` and `com.csweet.office.runtime.plist`. | `/Library/Application Support/CSweet/Setup/` — installer result files written for `--result-job-id`. |
| `/Library/Application Support/CSweet/{Execution,Office,ArtifactMedia,AgentRuntime,InstallerPayload}` — including the identity, Pfx, authorization state, `runtime-host.key`, artifact media, and Apple Virtualization instance state. | The enrolment record in C-Sweet; revoke it in fleet administration if it was not already revoked. |
| `/var/run/csweet-av`. | |
| The `_csweetnode` user and `_csweet` group. | |
| `/usr/local/sbin/csweet-configure-office` and `/usr/local/sbin/csweet-uninstall-office`. | |
| The pkg receipt (`pkgutil --forget com.csweet.office.payload`). | |

`/Library/Application Support/CSweet` itself is removed only when it is empty, so the presence of the `Setup`
subdirectory keeps it.

## Removing an Office safely

1. Drain the Office in C-Sweet and wait for active assignments to reach zero.
2. Revoke the enrollment in C-Sweet if the machine will not be re-enrolled under the same identity.
3. Run the platform uninstaller. Use `--force` / `-Force` only after step 2, and only when any remaining work
   may be destroyed.
4. Reinstall from a signed package. The uninstaller deletes the identity, so a reinstall enrolls as a new Office
   (`SEC-INV-18`).

A repair that fails is not a failure of removal: the guided flow keeps repair and removal as separate
administrator decisions, and removal is never executed unattended by the recovery service.

> **Out of repo:** revoking an Office, and setting up the administrator-authorized session that lets a removal
> helper run at all, are Headquarters actions. Office can only delete what is on the machine and report the
> result.

## Sources

`scripts/windows/{Uninstall-CSweetOffice.ps1,Remove-CSweetOfficeForRecovery.ps1,New-CSweetOfficeMsi.ps1,Install-CSweetOfficeRuntimeHost.ps1}`,
`scripts/linux/uninstall-office.sh`, `scripts/macos/uninstall-office.sh`, `scripts/linux/new-native-packages.sh`,
`src/CSweet.Office.Configurator/Program.cs`, `README.md`, `releases/0.5.0.md`,
`docs/20-security/06-host-privilege-model.md`.

Verified: 2026-09-15.
