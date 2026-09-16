# CSweet.Office.Configurator

The Windows enrollment handoff and the maintenance service host. It exists because enrollment happens from a
browser-issued `csweet-office://enroll/v1` URI: the Configurator parses that URI, re-launches itself elevated,
talks to the control plane's assisted-setup endpoints, and then runs the shipped installer with the values
Headquarters returned. With `--maintenance-service` the same executable becomes `CSweet.Office.Maintenance`,
the third Windows service, which polls for authorized repair and upgrade requests. It has no project
references and shares nothing with the runtime libraries.

## Project facts

| Fact | Value |
|---|---|
| Output kind | console exe (`Microsoft.NET.Sdk`, `<OutputType>Exe</OutputType>`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | none |
| Key package references | `Microsoft.Extensions.Hosting.WindowsServices` |
| `AssemblyName` | `CSweet.Office.Configurator` (explicit) |
| `RootNamespace` | `CSweet.Office.Configurator` (explicit) |
| `InternalsVisibleTo` | `CSweet.Office.Tests` |
| csproj `<Description>` | absent |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `Program` | top-level statements | The handoff flow, with the elevation retry and the installer invocation. |
| `Arguments` | static class | `Uri(args)`: accepts a single argument or `--uri <value>`, and requires scheme `csweet-office`, host `enroll`, path `/v1`. `Query(uri)`: case-insensitive query parsing. |
| `OfficeMaintenanceService` | static class (internal) | Reads `--settings=<path>`, requires an HTTPS control-plane URL, and runs a Windows service host with `MaintenanceWorker`. |
| `MaintenanceSettings` | record (internal) | `ControlPlaneUrl`, `CertificateSha256`, `StateDirectory`, `ConfiguratorPath`. |
| `MaintenanceWorker` | `BackgroundService` (internal) | The 30-second maintenance poll: read `node-state.json`, load the durable identity, complete a certificate challenge/recovery, claim a maintenance handoff, validate it, and launch the Configurator with `--uri`. |

## Entry points / composition

`Program.cs` branches immediately:

1. Not Windows → exit code 2.
2. `--maintenance-service` present → `OfficeMaintenanceService.RunAsync(args)` and exit.
3. Parse the URI; if it is missing or malformed → exit 2.
4. If the process is not elevated: re-launch `Environment.ProcessPath` with `--uri <uri>` and `runas`, wait for
   it, and return its exit code. A cancelled elevation is exit code 3.
5. Read the handoff out of the fragment (`#handoff=…`), and require `session` (a GUID) and `origin`
   (absolute HTTPS) from the query; optionally read `certificate`, a SHA-256 fingerprint that pins the
   control-plane TLS certificate by comparing `SHA256.HashData(certificate.Export(X509ContentType.Cert))`.
6. The install root is `AppContext.BaseDirectory\..\..`, with one special case: a directory literally named
   `recovery` is skipped one level.
7. `Get-CSweetOfficeRecoveryState.ps1` is run to classify the existing installation (`none`, `clean`,
   `active`, `unsafe`), and for targeted operations the local `node-state.json` office id must match the
   `office` query value.
8. `POST api/offices/local-sessions/preflight` → `AssistedOfficePreflightResponse`. A `remove` action diverts
   to the staged removal path; otherwise the flow stops unless `ProceedToRedemption` is set.
9. `POST api/offices/local-sessions/redeem` → `RedeemAssistedOfficeSetupResponse`; the session id, setup
   receipt, and HTTPS control-plane URL are validated, and for non-maintenance runs an enrollment token must
   be present.
10. For maintenance actions the recovery app runs `Enter-CSweetOfficeMaintenance.ps1` first; for a fresh
    install the enrollment token is written to a temporary file and locked down with
    `icacls /inheritance:r /grant:r *S-1-5-18:F *S-1-5-32-544:F`.
11. `Install-CSweetOffice.ps1` runs with `-PayloadRoot`, `-ControlPlaneUrl`, `-AssistedSetupSessionId`,
    allocations, `-ExistingInstallationAction`, progress arguments, `-NonInteractive -Elevated`, and
    `-EnrollmentTokenInputPath` when applicable; the certificate pin is passed when Headquarters supplied one.
12. Failures post `POST api/offices/local-sessions/result` with one of `office_setup_failed`,
    `office_removal_failed`, or `office_upgrade_completed`; the temporary token file is deleted in a `finally`.

The maintenance service path is separate and simpler: `MaintenanceWorker` builds two HTTP clients — a
server-authenticated one and a mutual-TLS one built from the recovered certificate — completes the identity
challenge/recovery, calls `POST api/offices/{officeId}/maintenance/claim`, validates the returned handoff with
`ValidHandoff`, and starts `MaintenanceSettings.ConfiguratorPath` with `--uri`. It does not require the
workload service to be running or its operational certificate to be valid.

## Behaviour worth knowing

- **The handoff URI shape is fixed.** Scheme `csweet-office`, host `enroll`, path `/v1`, `#handoff=…`,
  and query keys `session`, `origin`, optional `certificate`, and for maintenance `office` plus `operation`.
  `ValidHandoff` re-checks that the handoff targets the same office id and the same control-plane authority and
  path as the configured origin, and that the operation is exactly `repair` or `upgrade`.
- **Headquarters cannot pass a command line.** The maintenance worker only ever launches the configured
  `ConfiguratorPath` with a fixed `--uri` argument; the record comment states that the executable path is
  written by the elevated installer in its protected configuration
  ([SEC-INV-19](../../20-security/11-security-invariants.md)).
- **Secrets are files, never arguments.** The enrollment token is written with `FileOptions.WriteThrough`,
  ACLed to SYSTEM and Administrators only, passed to the installer as a path, and deleted afterwards. This is
  the same rule the tests assert for the Linux installer's token handling.
- **Certificate pinning is optional and additive.** When the `certificate` parameter (or the maintenance
  settings) supplies a SHA-256 fingerprint, server validation is replaced with a fixed-time comparison against
  it; otherwise the platform trust store decides. `AllowAutoRedirect = false` on both clients.
- **Removal is staged into an ACL-protected directory.** `StageRemoval` copies the helper and uninstaller
  scripts into `%PROGRAMDATA%\CSweet\Setup\remove-<guid>`, writes the handoff and setup receipt as separate
  secret files, applies `icacls /inheritance:r` with SYSTEM and Administrators full control, and starts the
  uninstaller detached with the parent process id.
- **Exit codes are meaningful**: 2 for usage or platform errors, 3 for a failed elevation, 4 for a rejected
  preflight/redeem response, 5 for a failed installer run, 6 for a failed removal.
- **The maintenance service retries every 30 seconds** and catches only expected exception families
  (`HttpRequestException`, `IOException`, `CryptographicException`, `JsonException`,
  `InvalidOperationException`, `Win32Exception`, `TaskCanceledException`, `ArgumentException`), logging the
  exception type without the message.

> **Out of repo:** every `/api/offices/local-sessions/*` and `/api/offices/{id}/maintenance/*` contract,
> all `CSweet.Office.Contracts.ControlPlane` request and response types used here, and the assisted-setup
> session semantics are Headquarters behaviour. The installer and uninstaller scripts this executable runs are
> repository content but are documented under operations and scripts, not here.

## Related tests

| Test class | What it pins |
|---|---|
| `MaintenanceHandoffTests` | `MaintenanceWorker.ValidHandoff` accepts only the predefined `repair` and `upgrade` operations, and rejects everything else. |
| `OfficeMaintenanceStateTests` | The drain and assignment state files this executable's maintenance flow depends on. |
| `WindowsHyperVOnboardingTests` | Several installer-script assertions that cover the assisted installer path the Configurator invokes, including token deletion, allocation mapping, and recovery/removal staging. |

## Related documentation

- [10-system/01-what-is-office.md](../../10-system/01-what-is-office.md) — where the assisted setup fits in
  the system.
- [10-system/03-components.md](../../10-system/03-components.md) — the maintenance service description.
- [20-security/11-security-invariants.md](../../20-security/11-security-invariants.md) — `SEC-INV-19`.
- [../../40-operations/01-installation-windows.md](../../40-operations/01-installation-windows.md),
  [../../40-operations/05-repair-and-recovery.md](../../40-operations/05-repair-and-recovery.md), and
  [../../40-operations/06-uninstall-and-removal.md](../../40-operations/06-uninstall-and-removal.md).
- [../scripts.md](../scripts.md) — the scripts this executable invokes.

## Sources

`src/CSweet.Office.Configurator/CSweet.Office.Configurator.csproj`,
`src/CSweet.Office.Configurator/{Program.cs,OfficeMaintenanceService.cs}`,
`tests/CSweet.Office.Tests/{MaintenanceHandoffTests,OfficeMaintenanceStateTests,WindowsHyperVOnboardingTests}.cs`.

Verified: 2026-09-15.
