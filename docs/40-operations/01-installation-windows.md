# Windows installation

**Audience:** Windows administrators installing an Office on a Windows host, and developers installing a
locally built payload.

Windows installs in one elevated run: the installer creates two virtual service accounts, writes a versioned
immutable package, pins Headquarters assignment trust over the verified TLS connection, starts the privileged
service, and drives enrollment. Installation is refused on a domain controller (`SEC-INV-17`), and it never
migrates a legacy identity (`SEC-INV-18`).

## Prerequisites

| Requirement | Evidence and failure mode |
|---|---|
| An elevated Windows PowerShell 5.1 session | `Install-CSweetOfficeRuntimeHost.ps1` calls `Assert-Administrator`. `Install-CSweetOffice.ps1` re-launches itself with `-Verb RunAs` when it is not elevated. |
| Not a domain controller | `Assert-NotDomainController` reads `Win32_ComputerSystem.DomainRole` and throws for 4 or 5. |
| A supported Windows edition | `WindowsHyperVHostProbe.IsSupportedEdition` accepts `EditionID` values starting with `Professional`, `Enterprise`, `Education`, or `Server`, and exactly `IoTEnterprise`. |
| Hyper-V hardware and feature | The Hyper-V helper's `probe` returns `unsupported-edition`, `hardware-requirements`, `hyperv-disabled`, or `restart-required` (`WindowsHyperVHostReadiness.HardwareRequirementsSatisfied` needs a present hypervisor, or SLAT plus firmware virtualization plus DEP plus at least 4 GiB of RAM). |
| A signed payload directory | `-PayloadRoot` must contain `runtime-manifest.json` (schema 1, 1–1000 file entries, lowercase per-file SHA-256) and every file it lists. |
| A control plane reachable over HTTPS | `ControlPlaneUrl` must be an absolute `https` URL. The installer probes its certificate before pinning anything. |

Hyper-V enablement is a separate, guided step. Office's provisioner runs `dism.exe /Online /Enable-Feature /All
/FeatureName:Microsoft-Hyper-V /NoRestart` after an elevation prompt; it refuses with `unsupported_edition`,
`hardware_requirements`, `interactive_session_required`, or `elevation_cancelled` when it cannot proceed.

> **Out of repo:** C-Sweet decides when to offer that guided enablement and shows the resulting instructions.
> Office supplies only `WindowsHyperVFeatureProvisioner` and the readiness probe.

## Two install paths

| Path | Entry point | Notes |
|---|---|---|
| **Developer bootstrap** | `.\scripts\windows\Install-CSweetOffice.ps1 -PayloadRoot <payload> -ControlPlaneUrl 'https://…'` | The payload is a locally built certification payload under `artifacts\windows-test\certification-*\payload`. C-Sweet reaches this path through `CSWEET_WINDOWS_ISOLATION_BOOTSTRAP` pointing at `Initialize-CSweetWindowsIsolationTest.ps1`. |
| **Packaged installer** | C-Sweet resolves `<C-Sweet base>\windows-runtime\Install-CSweetOfficeRuntimeHost.ps1` (or the `CSWEET_WINDOWS_RUNTIME_INSTALLER` override) beside a `payload` directory, and launches it elevated with `-PayloadRoot <script dir>\payload`, `-ControlPlaneUserSid`, `-ProgressPath`, and `-ProgressJobId`. | The same script the developer path uses, with the payload and progress wiring supplied by C-Sweet. When the session carries enrollment material, it also passes `-ControlPlaneUrl` and `-EnrollmentTokenInputPath` for a protected token file. |

The signed MSI (`scripts/windows/New-CSweetOfficeMsi.ps1`) packages the same seven installer scripts plus the
payload into `%ProgramFiles%\CSweet\Office` (scripts at the root, payload under `payload\`), registers the
`csweet-office` URI protocol against `payload\configurator\CSweet.Office.Configurator.exe`, and records the
product code under `HKLM\Software\CSweet\Office`. Building it requires WiX Toolset v4 (`wix`), Windows SDK
`signtool`, and a signing certificate; `MajorUpgrade` blocks a downgrade.

> **Out of repo:** release signing belongs to the hardened platform workflows. Do not build or sign a
> distributable MSI from an ordinary development runner.

### Stale payloads

Two failures mean the payload is older than the shipping code:

- The manifest's `officeExecutable` file version is below `0.1.0.0`. The installer refuses the payload with a
  message stating that it predates privileged signed-assignment enforcement and must be rebuilt with Office
  1.0.2 or later.
- The certificate probe times out after 20 seconds. That means the payload's Node predates
  `--probe-control-plane-certificate`; the message is *"Rebuild the Office payload with the current Node
  executable."*

Building the solution does not refresh an installed payload, and the installer skips any installed file whose
SHA-256 already matches the manifest, so re-running it against the same payload directory is a no-op rather
than a refresh.

## `Install-CSweetOffice.ps1` parameters

`Install-CSweetOfficeRuntimeHost.ps1` accepts the same parameters except `-Elevated`, plus `-InstallRoot`
(default `%ProgramFiles%\CSweet\Office`), `-DataRoot` (default `%ProgramData%\CSweet\Office`), and
`-ControlPlaneUserSid` (default: the current user's SID).

| Parameter | Type and default | Meaning |
|---|---|---|
| `-PayloadRoot` | `string`, required | Verified payload directory. Paths from the manifest are resolved beneath it and may not escape it. |
| `-ControlPlaneUrl` | `string`, required | Absolute HTTPS URL of the C-Sweet gateway. |
| `-ControlPlaneCertificateSha256` | `string`, empty | Trust pin for the control-plane leaf. Accepts `:`/`-` separators and a `sha256:` prefix; 64 hex characters after normalizing. |
| `-EnrollmentTokenInputPath` | `string`, empty | Protected file holding the one-use enrollment token (32–256 characters). Required for a `-NonInteractive` enrollment and for `reconnect`; forbidden for `upgrade`. The file is deleted after it is read. |
| `-AssistedSetupSessionId` | `guid`, empty | Correlates the install with the C-Sweet assisted setup session. Required by `reconnect`. |
| `-ExistingInstallationAction` | `none` \| `reconnect` \| `upgrade`, `none` | See [Existing installations](#existing-installations). |
| `-SecurityProfile` | `baseline` \| `hardened` \| `development`, `baseline` | See [08-security-postures.md](08-security-postures.md). |
| `-MixedUseHost` | `bool`, `true` | `false` declares a dedicated Office host. |
| `-AllowDevelopmentAssignments` | switch | Required when `-SecurityProfile development`. The installer throws otherwise. |
| `-AllocatableCpuCount` | `int`, `0` | `0` selects `max(1, ProcessorCount - 1)`. |
| `-AllocatableMemoryMb` | `int`, `0` | `0` selects `4096`. |
| `-AllocatableDiskMb` | `int`, `0` | `0` selects `32768`. |
| `-MaximumConcurrentWorkloads` | `int`, `0` | `0` selects `max(1, ProcessorCount / 2)`. |
| `-ProgressPath` | `string` | Must resolve to `%ProgramData%\CSweet\Setup\windows-isolation-<jobId:N>.json`; any other path is rejected. Defaults from `-ProgressJobId`. |
| `-ProgressJobId` | `guid`, empty | A fresh GUID is generated when omitted. |
| `-ProgressWorkflow` | `string`, `packaged-installer` | Free-text workflow label recorded in the progress file. |
| `-NonInteractive` | switch | Suppresses prompts. Requires `-EnrollmentTokenInputPath`, and requires `-ControlPlaneCertificateSha256` when the control plane uses a private CA. |
| `-Elevated` | switch (`Install-CSweetOffice.ps1` only) | Internal re-entry marker set by the self-elevation path. |

Allocatable values are written into the Node configuration and reported on enrollment; they are not enforced
locally.

## Control-plane trust

Enrollment fails closed unless the control plane's identity is established first. The installer runs two probes
from the payload's own `CSweet.Office.Node` executable, each with a 20-second timeout.

1. `--probe-control-plane-certificate <url>`. A hostname mismatch (the `RemoteCertificateNameMismatch` policy
   bit) and an expired or not-yet-valid leaf both fail immediately. Chain-building errors are tolerated — with a
   pin, the pin *replaces* PKI as the trust anchor.
2. `--probe-headquarters-assignment-trust <url> <fingerprint>`. The returned assignment signing key id and a
   base64 verification key of 64–1024 bytes are pre-written to
   `<DataRoot>\authorization\headquarters-trust.json` with `OfficeId` left empty, which is the one permitted
   pin transition (`SEC-INV-01`).

Interactive behaviour when the supplied fingerprint differs from the observed one, or when the control plane
presents a private CA:

| Situation | Behaviour |
|---|---|
| `-ControlPlaneCertificateSha256` given and equal to the observed fingerprint | Continue silently. |
| `-ControlPlaneCertificateSha256` given and different | Throw with both fingerprints. |
| No pin, certificate trusted by Windows | Continue; no pin is written. |
| No pin, private CA, non-interactive | Throw, telling the operator to re-run with `-ControlPlaneCertificateSha256 '<observed>'`. |
| No pin, private CA, interactive | Print URL, subject, issuer, validity window, and SHA-256, then require the operator to type `TRUST` (case-insensitive). Anything else aborts. |

The enrollment token is read only after the trust file is written. It is validated to 32–256 characters, stored
as `<DataRoot>\node\enrollment.secret`, and deleted by the Node after a successful claim.

## Existing installations

The installer always detects a registered `CSweet.Office.Node` service and refuses to silently replace it.

| `-ExistingInstallationAction` | Preconditions | Effect |
|---|---|---|
| `none` (default) | No existing Office, or an existing Office that is not being re-enrolled. Passing an enrollment token while an Office already exists fails with `existing_office_detected`. | First install or in-place refresh. |
| `reconnect` | An `-AssistedSetupSessionId` **and** fresh enrollment material are required; the recovery probe must report `clean`. | Stops the services and deletes `node`, `authorization`, `artifact-media`, `hyperv`, and `runtime-host.key` before enrolling as a new Office. Refuses with `existing_office_active` when work is present and `reconnect_unsafe` when state cannot be validated. `SEC-INV-18`. |
| `upgrade` | An existing identity and configuration, no enrollment material, drain state `draining`, zero active-assignment markers, and an upgrade-mode probe result of `clean`. | Replaces the package while keeping the identity. See [04-upgrade-and-drain.md](04-upgrade-and-drain.md). |

A first install (no existing Node service) additionally deletes the legacy `CSweet.ExecutionNode` and
`CSweet.RuntimeHost` services, retires legacy VMs under `%ProgramData%\CSweet\AgentRuntime`, and removes
`%ProgramFiles%\CSweet\ExecutionNode`, `%ProgramFiles%\CSweet\RuntimeHost`, and
`%ProgramData%\CSweet\ExecutionNode`. Legacy identities and certificates are deliberately not migrated.

The legacy shared state layout is rejected outright: an Office whose `node-state.json` sits at
`%ProgramData%\CSweet\Office\node-state.json` instead of `%ProgramData%\CSweet\Office\node\node-state.json`
must be drained and reinstalled.

## What an install creates

| Artifact | Location |
|---|---|
| Immutable package | `%ProgramFiles%\CSweet\Office\<packageVersion>\`, with `appsettings.json` written at the version root. |
| Pristine recovery copy | `%ProgramFiles%\CSweet\Office\recovery\<packageVersion>\` plus `runtime-manifest.json`. |
| Installer scripts | The seven scripts copied to `%ProgramFiles%\CSweet\Office\`. |
| Maintenance settings | `%ProgramFiles%\CSweet\Office\maintenance-settings.json`, ACL-restricted to `SYSTEM` and `Administrators` (`SEC-INV-19`). |
| State roots | `%ProgramData%\CSweet\Office\{artifacts,artifact-media,hyperv,node,authorization}` and `runtime-host.key` (32 random bytes, base64). |
| Progress file | `%ProgramData%\CSweet\Setup\windows-isolation-<jobId:N>.json`. |
| Services | `CSweet.Office.RuntimeHost` (*C-Sweet RuntimeHost*, virtual service account, Automatic) and `CSweet.Office.Node` (*C-Sweet Office*, virtual service account, Manual until enrollment material exists, then Automatic). |
| Maintenance service | `CSweet.Office.Maintenance` (*C-Sweet Office Recovery*, `SYSTEM`, Automatic, running `CSweet.Office.Configurator.exe --maintenance-service "--settings=<install root>\maintenance-settings.json"`). |
| Failure actions | `reset= 86400, actions= restart/5000/restart/15000/none/0` for both Office services, and `restart/15000/restart/30000/restart/60000` for the maintenance service. |
| Hyper-V socket registration | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\00000ac9-facb-11e6-bd58-64006a7986d3`, `ElementName` = *C-Sweet authenticated agent broker*. The braced legacy key is removed. |
| Machine environment | `CSWEET_HYPERV_BROKER_SERVICE_ID`, `CSWEET_HYPERV_DATA_ROOT`, `CSWEET_ARTIFACT_MEDIA_ROOT`, and a matching `Environment` multi-string on the RuntimeHost service key. |
| ACLs | Non-inherited `RX` on the version root for both service SIDs with `Assert-FileReadExecuteAce` as an install gate (`SEC-INV-16`); `runtime-host.key` readable by the control-plane user SID, the Node SID, and the RuntimeHost SID; `artifacts` and `artifact-media` modify for the Node and read for the RuntimeHost; `hyperv` and `authorization` modify for the RuntimeHost only. |
| Hyper-V access | The RuntimeHost service SID is added to `Hyper-V Administrators` (`S-1-5-32-578`), and the virtual machines group `S-1-5-83-0` receives `RX` on the guest-image directory and `R` on the image. |
| Event Log | Application sources named `CSweet.Office.Node` and `CSweet.Office.RuntimeHost`, created before the services start and primed with an informational entry. |

The installer then waits up to 30 seconds for each service to reach `Running` and up to 60 seconds for
`<DataRoot>\node\node-state.json` to appear. When the Node logs an enrollment failure it extracts the error code
from the Event Log; `invalid_enrollment` is reported with the instruction to generate a fresh connection code in
C-Sweet.

## The progress file

`%ProgramData%\CSweet\Setup\windows-isolation-<jobId:N>.json` is the machine-readable record C-Sweet polls. Its
directory ACL is reset so only the control-plane user (read), `SYSTEM`, and `Administrators` can reach it.

| Field | Notes |
|---|---|
| `schemaVersion`, `jobId`, `workflow` | Correlation. `workflow` is the `-ProgressWorkflow` value. |
| `state` | `running`, `restart-required`, `completed`, or `failed`. |
| `phaseKey`, `phaseDisplayName`, `message`, `percentComplete` | Human-readable phase reporting. |
| `errorCode`, `errorMessage` | Populated on failure. `existing_office_detected`, `existing_office_active`, and `reconnect_unsafe` are lifted from the exception text; anything else becomes `runtime-install-failed`. |
| `ownerProcessId`, `startedAt`, `updatedAt` | Used by C-Sweet to detect a preparation that stopped or was interrupted by a restart. |

## Sources

`scripts/windows/{Install-CSweetOffice.ps1,Install-CSweetOfficeRuntimeHost.ps1,CSweet.WindowsSetupProgress.ps1,New-CSweetOfficeMsi.ps1,Get-CSweetOfficeRecoveryState.ps1}`,
`src/CSweet.Office.Runtime.HyperV/{WindowsHyperVHostProbe.cs,WindowsHyperVHostReadiness.cs,WindowsHyperVFeatureProvisioner.cs,WindowsRuntimeHostProvisioner.cs,WindowsRuntimeHostProgressStore.cs}`,
`src/CSweet.Office.Runtime.HyperV.Helper/HyperVHelperController.cs`, `src/CSweet.Office.Node/OfficeOptions.cs`,
`README.md`, `docs/20-security/06-host-privilege-model.md`.

Verified: 2026-09-15.
