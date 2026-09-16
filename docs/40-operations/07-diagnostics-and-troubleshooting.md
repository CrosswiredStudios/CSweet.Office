# Diagnostics and troubleshooting

**Audience:** administrators and support engineers diagnosing a failed install, a failed enrollment, or a failed
workload.

Every symptom below has a named source of truth. Start by identifying which process failed, then read the log for
that process. Most Office failures are reported twice: once as a stable code to Headquarters, and once as a
local log entry naming the underlying cause.

## Where the logs are

| Platform | Location |
|---|---|
| Windows, Node | Application Event Log, source `CSweet.Office.Node`. `Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='CSweet.Office.Node'}`. |
| Windows, RuntimeHost | Application Event Log, source `CSweet.Office.RuntimeHost`. Both sources are pre-created by the installer and primed with an informational entry, so logs appear even if the process dies early. |
| Windows, maintenance service | Application Event Log via the Windows Service lifetime (`CSweet.Office.Maintenance`). The installer does not pre-create this source. |
| Windows, installer | `%ProgramData%\CSweet\Setup\windows-isolation-<jobId:N>.json` — `state`, `phaseKey`, `message`, `errorCode`, `errorMessage`, `percentComplete`, `ownerProcessId`. |
| Windows, assignments | The Node log, keyed by assignment id: `office-error` deliberately tells the reader to *"Review the Office Node service log using the assignment identifier."* |
| Linux | `journalctl -u csweet-office-node.service` and `journalctl -u csweet-office-runtime.service`. |
| macOS | The unified log; neither daemon plist defines `StandardOutPath` or `StandardErrorPath`, so use `log show --predicate 'process == "CSweet.Office.Node"'` (and `CSweet.Office.RuntimeHost`). |

## Symptom → cause → check

| Symptom | Likely cause | Check |
|---|---|---|
| `CSweet.Office.RuntimeHost` will not start | A package or state ACL that the service identity cannot traverse, a missing Hyper-V socket registration, an absent `Environment` multi-string, or a nearly-created Event Log source. | Run `scripts/windows/Diagnose-CSweetOfficeRuntimeHostStart.ps1` (below). During an install, the installer already surfaces the first `Error`-level Event Log entry for the service from the last five minutes. |
| The RuntimeHost service is running but every workload fails with `isolation-provider-unavailable` | The Hyper-V `probe` returned a typed failure, so the fail-closed selector had no candidate. | `Get-WinEvent -ProviderName CSweet.Office.RuntimeHost`, then confirm the probe prerequisites in [01-installation-windows.md](01-installation-windows.md#prerequisites). The helper's reason is passed through verbatim, for example *"The Hyper-V Windows feature is not enabled."* |
| Hyper-V is unavailable with `unsupported-edition`, `hardware-requirements`, `hyperv-disabled`, `restart-required`, or `broker-transport-unavailable` | Windows edition, firmware virtualization and SLAT, the `Microsoft-Hyper-V` feature, a pending restart, or the GuestCommunicationServices registration. | Re-run the guided enablement, then restart. `restart-required` means the hypervisor is not present yet. `broker-transport-unavailable` means the registry key that maps service id `00000ac9-facb-11e6-bd58-64006a7986d3` is missing. |
| The probe fails while Hyper-V is obviously healthy | The helper runs as the RuntimeHost service identity, so its effective access is what counts. | `Add-LocalGroupMember -SID S-1-5-32-578 -Member "NT SERVICE\CSweet.Office.RuntimeHost"` is the installer's normal action; also confirm the virtual machines group `S-1-5-83-0` still has `RX` on the guest-image directory and `R` on the image (`SEC-INV-16`). |
| Office never enrolls and the installer times out after 60 seconds | An expired or already-used token, an unreachable or untrusted control plane, or a name mismatch. | Compare `%ProgramData%\CSweet\Office\node\enrollment.secret` with the token in C-Sweet; look for `Office enrollment failed (<code>): <message>` in the Node's Event Log. `invalid_enrollment` means the code must be regenerated in C-Sweet. Also re-run the payload's `CSweet.Office.Node --probe-control-plane-certificate <url>`. |
| Enrollment fails with `existing_office_detected`, `existing_office_active`, or `reconnect_unsafe` | An existing Office is installed; work is present; or the existing state could not be validated. | Read `errorCode` in the progress file. `existing_office_active` requires draining; `reconnect_unsafe` usually means a missing or unreadable `node-state.json`, an unparseable service command, or a mismatched configuration. |
| The operational certificate expired and the Node cannot connect | The Node's recovery path needs the original enrolled private key, an approved non-revoked enrollment, and a reachable gateway. | Check `%ProgramData%\CSweet\Office\node\node-identity.pfx` exists and its ACL still admits the Node SID; confirm the enrollment is approved and not revoked in C-Sweet; confirm the gateway the Office talks to is the one that approved it (multi-gateway installations need connection affinity for the challenge/recovery exchange). |
| Recovery fails on the machine but the maintenance service is running | The recovery package on this host predates the guided flow. | Confirm `Enter-CSweetOfficeMaintenance.ps1` exists in the install root. Office 0.3/0.4 installations need the current signed recovery package installed locally with an administrator approval first; the configurator reports *"The recovery app needs to be updated."* |
| A workload fails with `certification expired` in its detail | The payload's certification window ended, so the selector reported *"<provider>: no active matching certification"* even though the probe passed. | Compare `certificationExpiresAt` in the installed `runtime-manifest.json` with the clock, then install a payload with a current certification. Other evidence faults read *"The installed certification evidence does not match its configured digest"*, *"No certification evidence is configured for this provider build"*, or *"The certification evidence is malformed or is not bound to this exact provider, image, protocol, and certification window."* |
| The workload ran but `LogExcerpt` is empty on Windows | The Hyper-V helper's `logs` operation is a stub that returns an empty array. | Expected. Use the guest's own `runtime.logs` for Firecracker and builder runs, and the Event Log for the host side. |
| A VM is left behind after a crash | Reaping is asymmetric: builder instances are never reaped on any platform, toolchain and runtime instances are, and Apple Virtualization reaps runtime instances only. | `Get-VM` and match configuration or disk paths beneath `%ProgramData%\CSweet\Office\hyperv`; remove by hand, or let the uninstaller do it (it removes every VM whose paths lie under the Office roots). |
| The installer refuses the payload as stale | The payload's embedded Office predates signed-assignment enforcement, or its Node predates `--probe-control-plane-certificate`. | Read the exception text, and check `FileVersion` of the executable named by `officeExecutable` in `runtime-manifest.json`. Rebuild the payload; the installer skips files whose digest already matches, so re-running it against the same directory cannot refresh an install. |
| A development build fails with missing projects | The sibling repositories are absent. | The primary solution loads `../CSweet.Office.Contracts`; `scripts/windows/New-CSweetHyperVTestGuest.ps1` needs `../CSweet.Isolation` unless `-IsolationRoot` is supplied. Note that `UseLocalOfficeContracts` silently prefers a sibling checkout, so local test results can differ from CI. |
| Work succeeds but you need proof the whole path works | — | Run `scripts/office-e2e.ps1` (below). |

## Failure codes

These are the stable codes Headquarters renders for a failed assignment. The Node produces them in
`OfficeWorker.DescribeExecutionFailure`, except the two status-level codes.

| Code | Produced when | Meaning and what to check |
|---|---|---|
| `assignment-envelope-invalid` | The signed assignment failed local validation before execution. The session stays open. | Contract or key drift between Headquarters and Office. Confirm the pinned `assignmentSigningKeyId` matches what Headquarters signs with (`SEC-INV-01`), and that both sides use compatible contract versions. |
| `isolation-provider-unavailable` | An `IsolationUnavailableException` — no certified provider survived selection, or the provider is not installed. | The message is the provider's own reason and is passed through verbatim. See the Hyper-V rows above. |
| `headquarters-broker-rejected` | `RpcException` with `FailedPrecondition` while opening the guest broker session. | Headquarters rejected the authenticated broker session. The detail comes from Headquarters (control characters stripped, 1500-character cap) and usually names the boot configuration or lease problem. |
| `headquarters-authorization-rejected` | `RpcException` with `PermissionDenied` or `Unauthenticated`. | The Office authorization was refused: revoked enrollment, expired authorization window, or a fencing epoch Headquarters no longer accepts. No detail is returned by design. |
| `headquarters-unavailable` | `RpcException` with `Unavailable` or `DeadlineExceeded`. | The control-plane connection dropped mid-workload. Check the gateway and the network path; the Node reconnects on a flat 5-second retry. |
| `headquarters-rpc-error` | Any other `RpcException`. | A protocol-level failure; the status code is included and nothing else. |
| `workload-failed` | The workload's own status: it exited, was stopped, or reported failure, and carried no error code. | Inspect the guest's `runtime.logs`. The provider's `LogExcerpt` is empty on Windows. |
| `office-error` | Any other exception. | A generic message naming the exception type, deliberately without the original text. Use the assignment identifier in the Node service log. |

`Completed` requires a non-failed state, exit code 0, and a termination reason of `None` or `Completed`; anything
else is `Failed` with `status.ErrorCode ?? "workload-failed"`.

## One-shot checks

| Check | Command |
|---|---|
| Office recovery state (`none`/`clean`/`active`/`unsafe`) | `.\scripts\windows\Get-CSweetOfficeRecoveryState.ps1` |
| The same probe in upgrade mode | `.\scripts\windows\Get-CSweetOfficeRecoveryState.ps1 -ForUpgrade` |
| Validate the probe itself | `.\scripts\tests\Test-OfficeUpgradeProbe.ps1` — stubs services, configuration, and Hyper-V cmdlets and asserts eight scenarios, so a probe change can be verified without a Hyper-V host. |
| Service state | `Get-Service CSweet.Office.Node, CSweet.Office.RuntimeHost, CSweet.Office.Maintenance` |
| Repair the ACL class of failure | `.\scripts\windows\Repair-CSweetOfficeRuntimeHostAccess.ps1 -ControlPlaneUserSid <sid>` — see [05-repair-and-recovery.md](05-repair-and-recovery.md). |

### Tracing a RuntimeHost start failure

`scripts/windows/Diagnose-CSweetOfficeRuntimeHostStart.ps1` requires administrator rights, downloads Process
Monitor from `-ProcessMonitorDownloadUrl` (default `https://download.sysinternals.com/files/ProcessMonitor.zip`),
rejects the download unless it carries a valid Microsoft signature, captures for a few seconds while running
`sc.exe start CSweet.Office.RuntimeHost`, converts the capture to CSV, and writes a filtered summary.

| Output | Meaning |
|---|---|
| `<OutputRoot>\<yyyyMMdd-HHmmss>\runtimehost-start.csv` | The full Process Monitor capture. |
| `<OutputRoot>\<yyyyMMdd-HHmmss>\access-denied.txt` | Rows with `ACCESS DENIED`, `PRIVILEGE NOT HELD`, or `BAD IMPERSONATION LEVEL` from `services.exe` or `CSweet.Office.RuntimeHost.exe`, or on paths matching `CSweet`, `Office`, or `RuntimeHost`. *"No matching access-denied rows were found"* means the cause is not a filesystem ACL. |

`-OutputRoot` defaults to `%ProgramData%\CSweet\Diagnostics\RuntimeHostStart`.

### Live smoke test

`scripts/office-e2e.ps1` drives a complete agent lifecycle against a running control plane and an enrolled
Office: health and runtime settings, an immutable repository import preview, an approved installation with
manifest-bounded grants, a wait for the isolated build, an immediate schedule tick, a wait for the runtime to
reach a terminal status, and history verification before disabling the test installation. It fails loudly when
the build or the run does not succeed, and prints the build log or the runtime reason.

```powershell
.\scripts\office-e2e.ps1 -RepositoryUrl <git repository> -BaseUrl 'http://localhost:8080'
```

| Parameter | Default | Purpose |
|---|---|---|
| `-RepositoryUrl` | required | The repository to import as a QA agent. |
| `-BaseUrl` | `http://localhost:8080` | The C-Sweet API base. |
| `-BusinessId` | `e2e-agent-runtime` | The business the QA installation belongs to. |
| `-TickFrequencySeconds` | `300` | Raised to the server minimum if it is lower. |
| `-TimeoutSeconds` | `900` | Per-phase wait limit for the build and the run. |

> **Out of repo:** the endpoints it calls (`/api/health`, `/api/agents/…`, `/api/agent-runtime/settings`) and the
> QA fixture they need are C-Sweet's. The script is a client of that API, not part of the Office runtime.

## Sources

`src/CSweet.Office.Node/{OfficeWorker.cs,ProviderInventory.cs}`, `src/CSweet.Office.Runtime.Core/{FailClosedIsolationProviderSelector.cs,ExternalPlatformIsolationBackend.cs,PlatformRuntimePayloadManifest.cs}`,
`src/CSweet.Office.Runtime.HyperV.Helper/HyperVHelperController.cs`, `src/CSweet.Office.Runtime.HyperV/WindowsHyperVHostProbe.cs`,
`scripts/windows/{Diagnose-CSweetOfficeRuntimeHostStart.ps1,Get-CSweetOfficeRecoveryState.ps1,Repair-CSweetOfficeRuntimeHostAccess.ps1,Install-CSweetOfficeRuntimeHost.ps1}`,
`scripts/tests/Test-OfficeUpgradeProbe.ps1`, `scripts/office-e2e.ps1`, `README.md`,
`docs/20-security/12-known-limits-and-tradeoffs.md`, `docs/30-workloads/02-runtime-workload-lifecycle.md`.

Verified: 2026-09-15.
