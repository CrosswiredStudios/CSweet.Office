# Status and error codes

**Audience:** operators reading a log or an installer message, and contributors adding a new code.

Every code in this repository is a lowercase, hyphen-separated token. Codes are grouped below by the
component that produces them, because the same symptom can be reported differently at each hop: a helper
rejection becomes a dispatcher `provider-*` code, which becomes a Node failure code, which is what
Headquarters and the operator finally see.

> **Out of repo:** the codes a Headquarters rejection carries over the wire (HTTP bodies, gRPC statuses, and
> the `ClaimOfficeResponse.ErrorCode` values such as `invalid_enrollment`) are produced by C-Sweet. Where a
> local code is the *translation* of a remote one, the mapping is stated.

## Assignment status values

Written by the Node into `AssignmentStatusUpdate.status`
(`src/CSweet.Office.Node/OfficeWorker.cs`); read by Headquarters.

| Status | When | Failure code and detail |
|---|---|---|
| `Starting` | Sent after the artifact grant is honoured and before `CreateAuthorizedAsync`. | none |
| `Running` | Sent after the guest broker channel is open and the relay has started. | none |
| `Completed` | Sent when inspect reports `Destroyed`, `Failed`, or `Stopped` with `ExitCode == 0` and a termination reason of `None` or `Completed`. | none |
| `Failed` | Sent with a failure code in every other terminal case, and from the catch-all handler. | see the Node failure codes below |

A `Failed` status from the inspect loop carries `status.ErrorCode` when the provider supplied one, otherwise
the literal `workload-failed` with the sanitized detail *"The isolated workload did not complete
successfully."*

## Node failure codes (`OfficeWorker.DescribeExecutionFailure`)

| Code | Trigger | Sanitized detail |
|---|---|---|
| `assignment-envelope-invalid` | `ValidateAssignment` threw `InvalidDataException` before execution. | *"The Office rejected the signed assignment envelope. Verify that headquarters and Office use compatible contract versions."* |
| `isolation-provider-unavailable` | `IsolationUnavailableException` (provider missing, not certified, or not a RuntimeHost client). | The exception message verbatim, sanitized. |
| `headquarters-broker-rejected` | gRPC `FailedPrecondition`. | *"Headquarters rejected the authenticated guest broker session: &lt;detail&gt;"* |
| `headquarters-authorization-rejected` | gRPC `PermissionDenied` or `Unauthenticated`. | *"Headquarters rejected the Office authorization."* |
| `headquarters-unavailable` | gRPC `Unavailable` or `DeadlineExceeded`. | *"The secure connection to Headquarters was unavailable while the workload was running."* |
| `headquarters-rpc-error` | Any other `RpcException`. | *"The secure Headquarters connection failed (&lt;StatusCode&gt;)."* |
| `office-error` | Any other exception. | *"The Office could not execute the workload (&lt;ExceptionType&gt;). Review the Office Node service log using the assignment identifier."* |
| `workload-failed` | Terminal inspect result with no provider error code. | *"The isolated workload did not complete successfully."* |

Sanitization keeps `\r`, `\n`, and `\t`, drops other control characters, truncates at 1500 characters, and
substitutes *"No additional detail was provided."* for an empty result. `OfficeWorkerFailureTests` pins that
the broker case stays actionable, that an unexpected exception never leaks its message, and that an isolation
failure keeps its exact text.

## Local RPC rejection codes

Produced by `RuntimeHostRequestAuthenticator.Validate`
(`src/CSweet.Office.Runtime.Protocol/RuntimeHostAuthentication.cs`). The code is logged by
`RuntimeHostRpcServer` and **no response frame is written**; the connection is closed (`SEC-INV-13`).

| Code | Check that failed |
|---|---|
| `unknown-key` | `authentication_key_id` differs from the configured `KeyId` (ordinal comparison). |
| `invalid-authentication-envelope` | Nonce is not 32 bytes, or signature is not 32 bytes. |
| `invalid-timestamp` | `authentication_timestamp_unix_seconds` is outside the `DateTimeOffset` range. |
| `expired-request` | Absolute difference from now exceeds the clamped clock skew. |
| `invalid-signature` | HMAC did not match (constant-time comparison). |
| `replayed-request` | The nonce was already accepted and has not expired from the replay cache. |

A frame whose `protocol_version` is not `1.0` is rejected before authentication and is logged without an
authentication code.

## Authorization gate rejections

`RuntimeHostAuthorizationGate` throws `InvalidDataException`; `RuntimeHostRequestDispatcher` maps every
rejection to `authorization-rejected` with the sanitized message
*"Headquarters workload authorization was rejected (diagnostic request: &lt;request id&gt;)."* The exact
reason stays in the RuntimeHost log.

| Condition | Message |
|---|---|
| No `authorization` field | *"A signed workload authorization is required."* |
| Trust file missing | *"Headquarters assignment trust has not been pinned."* |
| Authorization version ≠ `AssignmentEnvelope.CurrentAuthorizationVersion` | *"The workload authorization version is unsupported."* |
| Office, assignment, or workload id invalid | *"The workload authorization identifiers are invalid."* |
| Signing key id ≠ pinned id | *"The workload authorization signing key does not match pinned Headquarters trust."* |
| Provider id ≠ request provider | *"The workload authorization provider does not match the platform request."* |
| Workload id ≠ request workload id | *"The workload authorization identifier does not match the signed workload specification request."* |
| Issued in the future, already expired, or longer than the permitted lifetime | *"The workload authorization is expired or outside its permitted lifetime."* |
| Specification digest ≠ `specification_sha256` | *"The signed workload specification digest is invalid."* |
| ECDSA verification failed | *"The workload authorization signature is invalid."* |
| Specification's own workload id differs | *"The signed specification has a different workload identifier."* |
| `previousEpoch >= fencingEpoch` for that assignment | *"The workload authorization was already accepted or fenced by a newer epoch."* |
| Handle outside the authorized scope | *"The provider returned a handle outside the authorized workload scope."* |
| Handle for an expired lease | *"The provider returned a handle for an expired workload authorization."* |

Trust-pin rejections: `PinHeadquartersTrustRequest` failures (`InvalidDataException`,
`CryptographicException`, or `IOException`) become the response code `headquarters-trust-rejected` with
*"The privileged service rejected the Headquarters assignment trust."* The pin is write-once with the single
permitted `Guid.Empty → real OfficeId` transition (`SEC-INV-01`).

The validation order is trust → ids → window → digest → signature → commit and is load-bearing
(`SEC-INV-08`); the fencing-epoch comparison is strict and the ledger is never pruned (`SEC-INV-09`).

## Dispatcher error codes

Produced by `RuntimeHostRequestDispatcher`. Codes appear in `WorkloadHandleResponse.error_code`,
`OperationResponse.error_code`, or `WorkloadStatusResponse.error_code` depending on the operation.

| Code | Operation | Meaning |
|---|---|---|
| `unsupported-operation` | any unhandled body arm | *"The requested runtime-host operation is not supported."* |
| `headquarters-trust-rejected` | pin trust | See above. |
| `provider-not-registered` | create, start, inspect, stop, destroy, read logs | No backend with that provider id; also returned when the handle does not resolve. |
| `guest-channel-unavailable` | create | The provider has no certified guest-channel connector installed. |
| `authorization-rejected` | create | The authorization gate rejected the request. |
| `provider-unavailable` | create | `IsolationUnavailableException` from the backend, with the provider's message verbatim. |
| `invalid-workload` | create | `ArgumentException`, `InvalidDataException`, or `InvalidOperationException` from the backend; the message is replaced by a correlated diagnostic. |
| `provider-create-failed` | create | Any other backend failure; correlated diagnostic. |
| `provider-inspect-failed` | inspect | Backend threw while inspecting; response also has `found = false`. |
| `invalid-grace-period` | stop | `grace_period_seconds` outside 0–300. |
| `workload-not-found` | start, stop, destroy | The backend reported a missing instance. |
| `provider-operation-failed` | start, stop, destroy | Any other backend failure; correlated diagnostic. |
| `guest-channel-unavailable` | open guest channel (server-side, not the dispatcher) | Handle unauthorized, no connector for the provider, or the connector threw `InvalidDataException`, `InvalidOperationException`, `IOException`, or `TimeoutException`. |

A probe for an unknown provider is not an error code: it returns `available = false` with
`unavailable_reason = "Provider is not registered."`. A probe whose provider lacks a certified guest-channel
connector is forced unavailable with the reason *"The provider does not have a certified guest-channel
connector installed."* and an empty `certification_json`.

Backend diagnostics that are surfaced verbatim use the form
`Platform helper rejected the operation (<helper code>): <detail>`, which
`RuntimeHostRpcIntegrationTests.IsolationFailurePreservesSanitizedProviderDiagnostic` pins exactly.

## Helper operation allow-list

`HelperArguments.Parse` accepts exactly `--protocol <version>` and `--operation <name>` in pairs and rejects
anything else with `HelperProtocolException("invalid-arguments", …)`.

| Helper | Operations |
|---|---|
| `CSweet.Office.Runtime.HyperV.Helper` | `probe`, `create`, `start`, `inspect`, `stop`, `destroy`, `reap`, `logs` (eight) |
| `CSweet.Office.Runtime.Firecracker.Helper` | the same eight plus `open-guest-channel` (nine) |

`SEC-INV-20` describes the Hyper-V helper's fixed eight-operation surface, protocol `1.0`, typed JSON on
stdio, and the per-invocation digest re-verification.

## Helper error codes

### Protocol-level (both helpers)

| Code | Meaning |
|---|---|
| `invalid-arguments` | Arguments are incomplete, empty, an odd count, or contain an unsupported switch or operation. |
| `unsupported-protocol` | The `--protocol` value is not `1.0`. |
| `request-too-large` | The stdio request exceeded its limit, including a delimiter-less oversize stream. |
| `invalid-request` | The request ended before its delimiter, was empty, or the framing was invalid. |
| `helper-failure` | The helper reported a typed failure at the process boundary. |

Helpers never exit non-zero for a typed failure, because RuntimeHost would discard the typed error
(`SEC-INV-20`).

### Hyper-V helper path-validation codes

`invalid-data-root`, `invalid-artifact-root`, `invalid-instance` (instance path escaped the data root). Each
is thrown as `HelperProtocolException`.

### Hyper-V helper typed failure codes (`Failure(...)`)

| Code | Meaning |
|---|---|
| `unsupported-operation` | Operation not in the eight-operation set. |
| `unsupported-host` | Not Windows. |
| `unsupported-edition` | Windows edition without Hyper-V. |
| `hardware-requirements` | Hyper-V hardware requirements not satisfied. |
| `hyperv-disabled` | Hyper-V optional feature not enabled. |
| `restart-required` | Hypervisor present but a restart is pending. |
| `broker-transport-unavailable` | The C-Sweet Hyper-V socket service is not registered. |
| `invalid-workload` | Not exactly one typed workload supplied. |
| `invalid-guest-image` | Guest image is not an existing absolute VHDX path. |
| `invalid-artifact-media` | Media missing, outside the approved root, failed its integrity check, or attached to a builder workload. |
| `invalid-vm-id` | Hyper-V returned an invalid VM identifier. |
| `create-failed` | VM creation failed (the underlying `HyperVCommandException` keeps its own code when it has one). |
| `not-found` | The workload was not found (`PowerShellHyperV` also translates exit code 44 to this). |
| `invalid-request` | Bounded log request is invalid. |
| `invalid-handle` | The workload handle could not be parsed. |
| `invalid-metadata` | The instance metadata failed validation. |

### `PowerShellHyperV` command codes

`unsupported-host`, `powershell-unavailable`, `powershell-start-failed`, `hyperv-timeout`,
`not-found` (exit code 44), `hyperv-command-failed` (any other non-zero exit, with sanitized stderr).

### Firecracker helper codes

| Code | Meaning |
|---|---|
| `unsupported-operation`, `unsupported-host` | as above, plus Linux-only. |
| `cgroup-v2-required`, `kvm-unavailable`, `data-root-unavailable` | Host prerequisites. |
| `firecracker-not-installed`, `kernel-not-installed`, `firecracker-unavailable`, `firecracker-version-mismatch` | Pinned binary, kernel, initrd, or version checks. |
| `invalid-workload`, `invalid-resources`, `invalid-guest-image`, `invalid-artifact-media` | Request validation. |
| `create-failed`, `start-failed`, `stop-failed`, `destroy-failed`, `inspect-failed`, `logs-failed` | Operation failures. |
| `not-found`, `invalid-handle`, `invalid-metadata`, `invalid-request` | Handle and metadata failures. |
| `workload-not-running` | Guest channel requested for a workload that is not running. |
| `broker-connect-rejected`, `broker-connect-failed` | The guest rejected the broker connection, or the channel is unavailable. |

Path-validation codes: `invalid-vsock-port`, `invalid-path`, `invalid-identity`, `invalid-cgroup`.

### stdio guest-channel handshake codes

The connector requires the helper's handshake line to advertise `stdio-duplex-v1`. A missing, empty, or
different transport value fails closed with `IsolationUnavailableException`; an oversized (>4096 bytes) or
CRLF-terminated handshake fails with `InvalidDataException`. A helper that reports a failure produces
*"The platform helper rejected the guest channel (&lt;code&gt;)."*

## Guest codes

### Guest boot failure reason codes

Emitted as `GuestBootFailure.reason_code` when the boot configuration cannot be used (`Program.cs`, only on the
host boot channel: `hyperv-vsock` or `firecracker-vsock`). The process then exits non-zero.

| Reason code | Exceptions mapped |
|---|---|
| `guest-boot-invalid` | `InvalidDataException`, `InvalidOperationException`, `FormatException` |
| `guest-boot-io-failed` | `IOException` |
| `guest-boot-failed` | anything else |

`detail` is the exception message with control characters removed, truncated to 512 characters.

### Guest exit codes

Emitted as `GuestExit.exit_code` with `reason_code` and `detail`.

| Exit code | Reason code | When |
|---|---|---|
| `0` | the host's `ShutdownCommand.reason_code` | Clean shutdown; the guest stops the workload with a 0–60 s grace period and returns. |
| `126` | `workload-start-failed` | The supervisor threw while starting the workload; the exception message is sanitized into `detail`. |
| `137` | `resource-limit-exceeded` | The bounded log drain detected that the workload exceeded `maximum_log_bytes` (`InvalidDataException`). |
| process exit code | `process-exited` | The workload exited on its own; `detail` is the sanitized 8 KiB diagnostic tail. |

### Guest lease rejection reason codes

| Reason code | Meaning |
|---|---|
| `challenge-expired` | The host challenge expired before the proof arrived. |
| `invalid-frame-limit` | The requested frame limit is outside 4096–16 MiB. |
| `invalid-guest-proof` | The ECDSA signature over the challenge payload did not verify. |

### Guest-side exceptions

The guest throws rather than coding these: `UnauthorizedAccessException("The host rejected the guest lease.")`
for a negative lease, `InvalidDataException("The host lease does not match the boot-bound guest lease.")` for a
lease-expiry mismatch, `InvalidDataException("The host sent an unsupported guest command.")` for an unknown
command, and `UnauthorizedAccessException("The local broker endpoint is not available to this workload.")` for
an unmapped proxy path.

## Installer, provisioning, and probe codes

### Windows RuntimeHost installer

| Code | Surface | Meaning |
|---|---|---|
| `[existing_office_detected]` | thrown message | An existing Office is installed but the install is not an explicit reconnect. |
| `[existing_office_active]` | thrown message | Reconnect refused: the recovery probe reported `active`. |
| `[reconnect_unsafe]` | thrown message | Reconnect refused: the probe reported neither `clean` nor `active`, or the enrollment material was missing. |
| `runtime-install-failed` | provisioning result `errorCode` | Catch-all failure code. |
| `invalid_enrollment` | enrollment failure code from Headquarters | The installer tells the operator to generate a new connection code in C-Sweet. |

The installer records progress through `CSweet.WindowsSetupProgress.ps1` with states
`running`, `restart-required`, `completed`, `failed`, and writes it to
`%ProgramData%\CSweet\Setup\windows-isolation-<jobId:N>.json`.

### Maintenance service

| Code | Meaning |
|---|---|
| `[maintenance_unsafe]` | The install root, data root, node state root, identity file, or maintenance folder failed its verification. |
| `[maintenance_busy]` | The recovery probe reported anything other than `clean`, before or after the Node was stopped. |

### Linux and macOS shell installers

| Exit code | Meaning |
|---|---|
| `1` | Not run as root. |
| `2` | Usage error, invalid option, invalid policy value, unsupported distribution, missing prerequisites, or invalid enrollment token. |
| `3` | Drain gate: `maintenance/drain-state` is not `draining`, or `maintenance/active-assignments/*.active` is non-empty. |

The Linux installer writes `/var/lib/csweet/setup/local-provisioning-<jobId>.result` with `completed` or
`failed` when `--result-job-id` is supplied; macOS writes the same content to
`/Library/Application Support/CSweet/Setup/local-provisioning-<jobId>.result`.

### Recovery-probe state values

`scripts/windows/Get-CSweetOfficeRecoveryState.ps1` prints exactly one of these as its last line:

| Value | Meaning |
|---|---|
| `none` | No Office service registered and no data root; a fresh install. |
| `clean` | Services and configuration are consistent, the content roots are inside the install root, and no assignment, handle, or VM state is owned. |
| `active` | Work exists: an `*.active` marker, an owned Hyper-V VM that is not `Off`, a live authorized handle, or a drain state other than `draining`. |
| `unsafe` | The state cannot be trusted: only one of the two services is registered, a content root escaped the install root, or the data root exists without either service. |

With `-ForUpgrade`, saved-state (`Saved`) Hyper-V VMs and powered-off (`Off`) instances with a matching
`authorized-workload-handles.json` record are tolerated, so a clean upgrade is not blocked by retained state.
`Test-OfficeUpgradeProbe.ps1` exercises the `none`/`clean`/`active`/`unsafe` transitions; see
[tests.md](tests.md#upgrade-probe-harness).

### Node maintenance markers

| Value | File | Meaning |
|---|---|---|
| `draining` | `node/maintenance/drain-state` | Dispatch is paused; upgrade and uninstall gates pass. |
| `ready` | `node/maintenance/drain-state` | Not draining. |
| `*.active` | `node/maintenance/active-assignments/` | One marker per in-flight assignment; cleared at the start of a new process session. |

## Codes produced by Headquarters rather than locally

| Surface | Produced by | Consumed as |
|---|---|---|
| `ClaimOfficeResponse.ErrorCode` | C-Sweet | `Office enrollment failed (<ErrorCode>): <Message>`; `invalid_enrollment` has a dedicated installer message. |
| gRPC status codes on `Connect`, `OpenWorkloadTunnel`, `DownloadArtifact` | C-Sweet | Translated into `headquarters-broker-rejected`, `headquarters-authorization-rejected`, `headquarters-unavailable`, or `headquarters-rpc-error`. |
| `AssignmentStatusUpdate.status` semantics | C-Sweet | The Node only writes the four status strings listed above. |
| `GateHouse`/gateway `FenceAssignment.reason` and `DrainOffice.reason` | C-Sweet | Logged verbatim; never interpreted locally. |
| `OfficeCertificateResponse.ErrorCode` | C-Sweet | Non-success is reported as *"Headquarters rejected Office identity recovery."* or the current certificate is kept. |

## Sources

`src/CSweet.Office.Node/{OfficeWorker.cs,ControlPlaneCertificateProbe.cs,OfficeStateStore.cs}`,
`src/CSweet.Office.Runtime.LocalRpc/{RuntimeHostRequestDispatcher.cs,RuntimeHostAuthorizationGate.cs,RuntimeHostRpcServer.cs}`,
`src/CSweet.Office.Runtime.Protocol/RuntimeHostAuthentication.cs`,
`src/CSweet.Office.Runtime.Core/{ExternalPlatformIsolationBackend.cs,ExternalPlatformStdioGuestChannelConnector.cs}`,
`src/CSweet.Office.Runtime.HyperV.Helper/{HelperArguments.cs,HyperVHelperController.cs,HyperVHelperPaths.cs,PowerShellHyperV.cs,Program.cs}`,
`src/CSweet.Office.Runtime.Firecracker.Helper/{HelperArguments.cs,FirecrackerHelperController.cs,FirecrackerHelperPaths.cs,Program.cs}`,
`src/CSweet.Office.RuntimeGuest/{Program.cs,GuestBrokerSession.cs,GuestWorkloadSupervisor.cs,GuestServiceOptions.cs,GuestLocalBrokerProxy.cs}`,
`scripts/windows/{Get-CSweetOfficeRecoveryState.ps1,Install-CSweetOfficeRuntimeHost.ps1,Enter-CSweetOfficeMaintenance.ps1,CSweet.WindowsSetupProgress.ps1,Uninstall-CSweetOffice.ps1}`,
`scripts/linux/{install-office.sh,uninstall-office.sh,configure-office.sh}`,
`scripts/macos/{install-office.sh,uninstall-office.sh}`,
`tests/CSweet.Office.Tests/{OfficeWorkerFailureTests.cs,RuntimeHostRpcIntegrationTests.cs,WindowsHyperVOnboardingTests.cs}`,
and the sibling contracts checkout `..\CSweet.Office.Contracts\src\CSweet.Office.Contracts\{Security\AssignmentEnvelope.cs,ControlPlane\EnrollmentContracts.cs}`.

Verified: 2026-09-15.
