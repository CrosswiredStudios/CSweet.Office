# Repair and recovery

**Audience:** Windows administrators recovering an Office that cannot enroll, cannot reach Headquarters, or has
lost its operational certificate.

Windows has a second, independent service whose only job is to accept repair and upgrade requests for its own
Office. It works when the workload service does not, because it never depends on the workload service — it
depends on the durable identity, its own outbound connection, and a handoff that Headquarters only issues to an
authorized administrator.

## The two flows

| Flow | Where it starts | What runs |
|---|---|---|
| **Guided recovery** (Office 0.5.0 and later) | C-Sweet **Settings > Offices > Repair connection**. | `CSweet.Office.Maintenance` claims the request and launches `CSweet.Office.Configurator.exe --uri <handoff>`. |
| **Local execution** | Any elevated prompt on the machine. | `Repair-CSweetOfficeRuntimeHostAccess.ps1`, the installer scripts, and the uninstaller, exactly as the guided flow would call them. |

> **Out of repo:** C-Sweet decides when a repair is offered, whether an administrator has authorized it, and
> what a repair is allowed to change. Office performs only the predefined local actions described here.

## The maintenance service

`CSweet.Office.Maintenance`, display name *C-Sweet Office Recovery*, runs
`CSweet.Office.Configurator.exe --maintenance-service "--settings=<InstallRoot>\maintenance-settings.json"` as
`SYSTEM` with automatic start and restart actions `restart/15000/restart/30000/restart/60000`. The settings file
lives in the administrator-owned install root and is ACL-restricted to `SYSTEM` and `Administrators`.

`maintenance-settings.json` carries `ControlPlaneUrl`, an optional `CertificateSha256` pin, `StateDirectory`
(the Node state directory), and `ConfiguratorPath`. The executable path is written by the elevated installer, so
Headquarters can request a predefined flow but never supply a command line.

Each cycle, once every 30 seconds:

1. Read `node-state.json` from the state directory and take its `OfficeId`.
2. Load `node-identity.pfx` — the durable, passwordless, exportable identity.
3. `POST api/offices/{officeId}/certificate/challenge`, sign the challenge with the identity's ECDSA key, then
   `POST api/offices/{officeId}/certificate/recover`. The returned certificate must be currently valid and its
   thumbprint must match the response, or the cycle fails.
4. Combine the recovered certificate with the private key and `POST
   api/offices/{officeId}/maintenance/claim`. `204 No Content` means there is nothing to do.
5. Validate the returned handoff URI, then launch the configurator with `--uri <handoff>` and wait for it to
   exit, logging the exit code.

Transient failures (`HttpRequestException`, `IOException`, `CryptographicException`, `JsonException`,
`InvalidOperationException`, `Win32Exception`, `TaskCanceledException`, `ArgumentException`) are logged as a
warning and retried on the next cycle.

### Why it works when the Node does not

- It is a **separate process with its own service registration**, so a stopped, crashed, or misconfigured
  `CSweet.Office.Node` does not take it down with it.
- It authenticates with the **durable identity**, and obtains a fresh operational certificate on every cycle.
  An expired or superseded operational certificate — the usual reason the Node cannot connect — is irrelevant to
  it.
- It **opens no inbound listener** and executes no arbitrary remote commands. Headquarters cannot reach into the
  host; the service reaches out. `SEC-INV-19`.

### Handoff validation

A handoff is rejected unless every one of these holds. A rejected handoff fails the cycle with
*"The maintenance handoff targeted another Office or server."*

| Element | Requirement |
|---|---|
| Scheme, host, path | `csweet-office`, host `enroll`, path `/v1`. |
| Fragment | Starts with `#handoff=`. |
| `office` | Exactly this Office's `OfficeId`, in `D` format. |
| `operation` | `repair` or `upgrade`. |
| `origin` | Absolute HTTPS URI whose authority **and** path match the configured control plane. |
| `session` | Parses as a GUID. |

## The configurator

When launched with `--uri`, the configurator demands administrator rights and elevates itself with `runas` if it
does not have them. It then runs a fixed sequence:

1. Parse the URI and require `office`, `operation ∈ {repair, upgrade, remove}`, `session`, and `origin`. If the
   URI names an Office, `node-state.json` must contain that same `OfficeId` (exit 4 otherwise).
2. `POST api/offices/local-sessions/preflight`. The response decides whether the flow continues, is a removal,
   or is a no-op.
3. `POST api/offices/local-sessions/redeem`.
4. For `repair` and `upgrade`: run `Enter-CSweetOfficeMaintenance.ps1 -OfficeId <officeId>` from the install
   root, then run `Install-CSweetOffice.ps1` with `-PayloadRoot <payload>` (the payload shipped alongside the
   configurator), `-ExistingInstallationAction upgrade`, `-NonInteractive`, `-Elevated`, the redeem response's
   allocation values, `-ProgressPath`/`-ProgressJobId` under `%ProgramData%\CSweet\Setup`, and **no** enrollment
   token. A successful maintenance run reports `office_upgrade_completed`.
5. For `remove`: stage the removal (see [06-uninstall-and-removal.md](06-uninstall-and-removal.md)).

| Exit code | Meaning |
|---|---|
| 0 | Success, or a preflight that intentionally stopped. |
| 2 | Usage, URL, or argument error. |
| 3 | Elevation was refused. |
| 4 | Preflight or redemption failed, or the response did not match the local Office. |
| 5 | Local preparation or installation failed. |
| 6 | Removal failed. |

## Repair in detail

Repair is drain-then-reinstall. It pauses dispatch and proves the machine is idle twice before it touches the
service:

1. `Enter-CSweetOfficeMaintenance.ps1` validates the install root, data root, and state file, rejects reparse
   points, and confirms the requested `OfficeId` is this Office (`maintenance_unsafe` otherwise).
2. It runs `Get-CSweetOfficeRecoveryState.ps1` and requires `clean` (`maintenance_busy` otherwise) — this is
   what sets the drain marker, so Headquarters stops dispatching.
3. It stops `CSweet.Office.Node`, waits up to 30 seconds, and **re-probes** to close the race with work that
   arrived in between.
4. Only then does it write `maintenance/drain-state` and return `clean`, letting the installer perform an
   identity-preserving upgrade of the verified local payload.

Identity, allocation, and capacity are retained. A newer version is installed only from a signed package that
was placed on the machine first — the recovery flow never downloads a payload.

`Enter-CSweetOfficeMaintenance.ps1` must exist in the install root. When it does not, the configurator throws
*"The recovery app needs to be updated."*:

> **One-time update:** Office 0.3 and 0.4 installations predate the maintenance service and the guided flow.
> They need the current signed recovery package installed locally, with a Windows administrator approval, before
> remote recovery becomes available. See `releases/0.5.0.md` and the root `README.md`.

## Access repair

`Repair-CSweetOfficeRuntimeHostAccess.ps1` fixes a specific class of failure: the services are registered but
the state or package ACLs no longer let them run. It requires administrator rights and `-ControlPlaneUserSid`,
and it refuses to touch anything that is not provably part of the installation:

| Step | Detail |
|---|---|
| Verify the service command | `ImagePath` must match `"<exe>" --contentRoot "<root>"`, both inside `-InstallRoot`, and the executable must be `<contentRoot>\runtime\CSweet.Office.RuntimeHost.exe`. |
| Stop orphaned processes | Any `CSweet.Office.RuntimeHost.exe` or `CSweet.Office.Node.exe` whose executable lives under the install root but is not the registered service process is stopped. |
| Re-register identities | `sc.exe sidtype <service> unrestricted` and `Win32_Service.Change` back to `NT SERVICE\<service>` for both services, plus Hyper-V Administrators membership for the RuntimeHost SID. |
| Repair access | Package, guest image, `runtime-host.key`, `artifacts`, `artifact-media`, `hyperv`, and `node` ACLs are rewritten, and `appsettings.json` is reset and given explicit `R` ACEs for the two service SIDs. |
| Verify configuration | `CSweet:Office:RuntimeHost` and the Hyper-V guest image path must be present and inside the content root; `AllowedClientSid` and `AllowedClientSids` are corrected to the supplied control-plane user SID plus the Node SID. |
| Restart | Both services are started and must reach `Running` within 30 seconds. |

Failures mark the progress file with `runtime-access-repair-failed`, attempt to restart both services, and exit
1. C-Sweet offers this as *"Repair secure agent runtime"* only when the payload's installer is present, its
script directory also contains `CSweet.WindowsSetupProgress.ps1`, and a previous preparation completed.

## Removal, and why it is never unattended

The guided flow can also remove an Office and restart it from scratch, but only as a separate, explicitly
authorized action:

- The configurator stages `Remove-CSweetOfficeForRecovery.ps1`, `Uninstall-CSweetOffice.ps1`, a handoff secret,
  and a setup receipt into `%ProgramData%\CSweet\Setup\remove-<guid>\`, ACL-restricted to `SYSTEM` and
  `Administrators`.
- The helper's first act is `Wait-Process -Id <configurator pid>`: teardown begins only after the process that
  requested it has exited, which detaches removal from the recovery channel that requested it.
- It then removes the product either through `msiexec /x <ProductCode> /qn /norestart
  CSWEET_FORCE_REMOVE=1` (accepting exit codes 0, 1605, and 1614) or, when no product code is registered, by
  running the uninstaller with `-Force -Elevated`.
- Both endpoints it calls are pinned. `Invoke-CSweetPinnedRemovalRequest` restricts every request to the
  configured origin and, when a fingerprint is available, replaces certificate validation with a fixed-time
  comparison of the leaf's SHA-256.
- Success posts `api/offices/local-sessions/removal-complete`; failure posts `office_removal_failed` to
  `api/offices/local-sessions/result` and rethrows. The staged secrets and scripts are deleted in a `finally`
  block.

`-Force` is used here because an authorized removal is an explicit decision that local work may be discarded —
not a convenience flag, and not something the recovery service may do on its own.

## Sources

`src/CSweet.Office.Configurator/{OfficeMaintenanceService.cs,Program.cs}`,
`scripts/windows/{Enter-CSweetOfficeMaintenance.ps1,Get-CSweetOfficeRecoveryState.ps1,Repair-CSweetOfficeRuntimeHostAccess.ps1,Remove-CSweetOfficeForRecovery.ps1,Install-CSweetOfficeRuntimeHost.ps1}`,
`src/CSweet.Office.Runtime.HyperV/WindowsRuntimeHostProvisioner.cs`, `README.md`, `releases/0.5.0.md`,
`docs/20-security/06-host-privilege-model.md`, `docs/20-security/11-security-invariants.md`.

Verified: 2026-09-15.
