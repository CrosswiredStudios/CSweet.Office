# CSweet.Office.Node

The unprivileged half of an Office. It enrolls the machine with Headquarters, owns the Office identity and
its certificate lifecycle, maintains the control session and the heartbeat, validates signed assignments
before executing them, downloads and verifies workload artifacts, and relays the authenticated guest-channel
byte stream between the guest and Headquarters. It never talks to a hypervisor and never writes RuntimeHost
state; its only privileged interaction is the local RPC client in `Runtime.LocalRpc`.

## Project facts

| Fact | Value |
|---|---|
| Output kind | worker service exe (`Microsoft.NET.Sdk.Worker`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `Runtime.Abstractions`, `Runtime.Artifacts`, `Runtime.LocalRpc`, `Runtime.Protocol` |
| Key package references | `Grpc.Net.Client`, `Microsoft.Extensions.Hosting.Systemd`, `Microsoft.Extensions.Hosting.WindowsServices`, `Microsoft.Extensions.Http` |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.Node`) |
| `InternalsVisibleTo` | `CSweet.Office.Tests` |
| csproj `<Description>` | absent |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `OfficeWorker` | `BackgroundService` | The whole control loop: enroll if needed, bump the session epoch, pin RuntimeHost trust, refresh the operational certificate, then run one control session. Also executes assignments and relays the guest channel. |
| `OfficeOptions` | class | `SectionName = "CSweet:Office:Node"`: control-plane URL and pin, enrollment token or token file, assisted-setup session id, state/cache/media directories, office name, allocatable CPU/memory/disk, maximum concurrent workloads, security profile, mixed-use flag, development-assignment consent, enabled and missing security controls; `SecurityPosture()`, `ResolveStateDirectory()`, `ResolveArtifactCacheDirectory()`, `ResolveArtifactMediaDirectory()`. |
| `OfficeStateStore` | class | Durable identity and maintenance state under the state directory: `node-state.json`, `node-identity.pfx`, `maintenance/drain-state`, `maintenance/active-assignments/*.active`. Creates the bootstrap ECDSA P-256 identity, produces the CSR PEM, and installs an issued operational certificate. |
| `OfficeState` | record | `OfficeId`, `EnrollmentReceipt`, `SessionEpoch`, `CertificatePath`, `AssignmentSigningKeyId`, `AssignmentVerificationPublicKeyBase64`. |
| `OfficeCertificateLease` | `IDisposable` (internal) | Holds the certificate the rotating TLS callback should use and keeps retired certificates alive for two minutes so in-flight handshakes cannot fail mid-rotation. |
| `ControlPlaneServerCertificateValidator` | class | Builds the pinned HTTP handlers. Public surface: `CreateHttpClientHandler(clientCertificate = null)`; internal: `CreateRotatingHttpHandler(currentCertificate)` and `Validate`. Reads the pin from configuration or from a trust file with `schemaVersion` 1 and `certificateSha256`. |
| `ControlPlaneCertificateProbe` | static class (internal) | The CLI modes that run before the host is built: `--probe-control-plane-certificate`, `--probe-headquarters-assignment-trust`, `--initialize-headquarters-assignment-trust`. Each returns an exit code the process forwards. |
| `OfficeArtifactCache` | class, `IAgentArtifactStore` | Content-addressed artifact cache plus media generation. `EnsureAsync` downloads through `OfficeGateway.DownloadArtifact` with three attempts, commits only after the reassembled digest matches, then ensures read-only ISO media. `ImportAsync` throws `NotSupportedException` — offices receive artifacts only through assignment-scoped grants. |
| `RuntimeHostInventory` | class | Probes the registered providers and produces `RegisterOfficeProviderRequest` rows for enrollment and heartbeat, including certification identity and a sanitized unavailable reason. `Platform()` reports `windows`, `linux`, or `macos`. |
| `HostPlatformProvider` | static class (internal) | Maps the current OS to one catalog descriptor; throws `PlatformNotSupportedException` on anything else. |

## Entry points / composition

`Program.cs` runs the certificate-probe shortcut first, then wires the host in this order:

1. `ControlPlaneCertificateProbe.TryRunAsync(args)` — if it returns an exit code, the process exits with it
   without starting the service.
2. `Host.CreateApplicationBuilder(args)`, `AddWindowsService` with service name `CSweet.Office.Node`, and
   `AddSystemd()`.
3. `OfficeOptions` from the `CSweet:Office:Node` section; the host refuses a `ControlPlaneUrl` that is not an
   absolute HTTPS URL.
4. `ControlPlaneServerCertificateValidator`, `OfficeStateStore`, `RuntimeHostInventory`, `OfficeArtifactCache`,
   `OfficeWorker`, `TimeProvider.System`.
5. A named `HttpClient` called `control-plane` whose primary handler comes from
   `ControlPlaneServerCertificateValidator.CreateHttpClientHandler()`.
6. `RuntimeHostEndpointOptions` (`Validate()` is called immediately) and `RuntimeHostAuthenticationOptions`,
   loading the shared key file from `%PROGRAMDATA%\CSweet\Office\runtime-host.key` when no key is configured.
7. `RuntimeHostRequestAuthenticator`.
8. `HostPlatformProvider.Resolve()` → one `RuntimeHostProviderClient` registered as `IAgentIsolationProvider`,
   constructed by the local `CreateClient` helper with the endpoint options, authenticator, and logger.

## Behaviour worth knowing

**The three gRPC RPCs on `OfficeGateway`.** The Node uses no other gRPC surface:

| RPC | Shape | Used for |
|---|---|---|
| `Connect` | bidirectional streaming (`OfficeControlMessage` out, `HeadquartersControlMessage` in) | The control session: heartbeat messages, lease renewals, assignment status updates out; `Hello`, `Assignment`, `Fence`, and `Drain` in. |
| `OpenWorkloadTunnel` | bidirectional streaming (`WorkloadTunnelFrame`) | The guest-channel relay, one call per running assignment. |
| `DownloadArtifact` | server streaming (`ArtifactChunk`) | Assignment-scoped artifact download into the Office cache. |

**The JSON endpoints.** All are HTTP against the control-plane base address, using the `control-plane`
client (pinned TLS, no client certificate) unless noted:

| Endpoint | Request | Response |
|---|---|---|
| `POST api/offices/claim` | `ClaimOfficeRequest` — token, name, machine, platform, architecture, version, protocol version, bootstrap thumbprint and serial, CSR PEM, allocations, provider inventory, security posture, assisted-setup session id. | `ClaimOfficeResponse` — office id, enrollment receipt, assignment signing key id, assignment verification public key. |
| `POST api/offices/{officeId}/certificate` | `OfficeCertificateRequest` — the enrollment receipt while the identity is still bootstrap, empty afterwards. | `OfficeCertificateResponse`. |
| `POST api/offices/{officeId}/certificate/challenge` | none | `OfficeCertificateChallengeResponse` — a 44-character challenge and its expiry. |
| `POST api/offices/{officeId}/certificate/recover` | `OfficeCertificateRecoveryRequest` — challenge plus an ECDSA P-1363 signature over the recovery payload. | `OfficeCertificateResponse`. |
| `POST api/offices/{officeId}/heartbeat` | `OfficeHeartbeatRequest` — receipt, session epoch, allocations, provider inventory, security posture, version. Sent once per session while the certificate is still bootstrap. | none (status code only). |
| `GET api/offices/assignment-trust` | none; used by the probe CLI mode, with the pinned certificate callback. | `assignmentSigningKeyId` and `assignmentVerificationPublicKeyBase64`. |

**Cadence.**

| Loop | Period | Detail |
|---|---|---|
| Control heartbeat | 10 s | `Task.WhenAny(readTask, Task.Delay(10s))` while the session runs; the first in-stream heartbeat of a session also carries the provider inventory. |
| Assignment lease renewal | 20 s | `AssignmentLeaseRenewal` asking for a 60 s extension, bound to the assignment id and fencing epoch. |
| Workload inspection | 2 s | `provider.InspectAsync` polls until a terminal state. |
| Certificate renewal check | 1 min | A 20 s timeout per attempt; failures log a warning and retry on the next tick. |

**Certificate rules.** Bootstrap is detected as `Subject == Issuer`. The renewal lead is
`min(6 hours, lifetime / 4)`. Renewal uses the bootstrap client while bootstrap and a mutual-TLS client
afterwards; a `401` from renewal triggers recovery instead. Recovery deliberately uses the plain
`control-plane` client, sends no client certificate and no receipt, and signs the challenge with the durable
identity key ([SEC-INV-02](../../20-security/11-security-invariants.md),
[SEC-INV-03](../../20-security/11-security-invariants.md)). The rotating handler sets
`AllowTlsResume = false` and a 20 s connect timeout, and `OfficeCertificateLease` keeps a retired certificate
for two minutes ([SEC-INV-04](../../20-security/11-security-invariants.md),
[SEC-INV-05](../../20-security/11-security-invariants.md)).

**Assignment handling.** Every inbound `HeadquartersControlMessage` must match both the local `OfficeId` and
`SessionEpoch`, or the session is torn down ([SEC-INV-06](../../20-security/11-security-invariants.md)). The
`Hello` key must match the key pinned at enrollment byte-for-byte. `ValidateAssignment` checks the
authorization version, key id, identifiers, fencing epoch ≥ 1, the ten-minute lifetime and two-minute future
skew, re-derives the specification digest, and verifies the ECDSA signature before anything starts. A rejected
envelope produces a `Failed` status with `assignment-envelope-invalid` and no provider call. `Fence`
cancels the matching assignment; `Drain` updates the durable drain marker.

**Execution path.** `ExecuteAssignmentAsync` acquires a workload slot, resolves the assigned provider,
deserializes the specification, downloads the artifact when the spec references one (the read grant must be
32-256 characters), sends `Starting`, requires the provider to be an `IRuntimeHostClient`, calls
`CreateAuthorizedAsync` with the assignment converted to `SignedWorkloadAuthorization`, starts it, requires it
to be an `IAgentGuestChannelProvider`, opens the guest channel, starts the tunnel relay task, and sends
`Running`. Then it renews the lease every 20 s and inspects every 2 s. Completion requires exit code 0 and a
`None` or `Completed` termination reason; otherwise the status is `Failed` with the provider's error code and
up to 64 KiB of logs.

**Failure-code mapping.** `DescribeExecutionFailure` is the single mapping point:

| Exception | `FailureCode` | Sanitized message |
|---|---|---|
| `IsolationUnavailableException` | `isolation-provider-unavailable` | The provider message, control characters stripped, 1500 characters maximum. |
| `RpcException` with `FailedPrecondition` | `headquarters-broker-rejected` | Names the headquarters broker session rejection and includes the detail. |
| `RpcException` with `PermissionDenied` or `Unauthenticated` | `headquarters-authorization-rejected` | "Headquarters rejected the Office authorization." |
| `RpcException` with `Unavailable` or `DeadlineExceeded` | `headquarters-unavailable` | The secure connection was unavailable while the workload was running. |
| Any other `RpcException` | `headquarters-rpc-error` | Includes the status code. |
| Anything else | `office-error` | Exception type name only, with a pointer to the Node service log. |

**The tunnel relay has a deliberate opening frame.** `RelayGuestChannelAsync` writes a sequence-0 frame with an
empty body before starting either copy direction. The code comment records why: Headquarters cannot start the
broker session until it receives a bound frame, and the Linux guest waits for boot configuration before
writing anything, so waiting for guest bytes first deadlocks all three parties. Download frames must arrive
with strictly increasing sequence numbers and the assignment's fencing epoch.

**The shared key is loaded, not generated.** The Node reads `%PROGRAMDATA%\CSweet\Office\runtime-host.key`
through `RuntimeHostAuthenticationOptions.LoadSharedKeyFileIfNeeded`; the installer writes that file so both
services hold the same secret. Rotation is an installer concern, not a code path here.

> **Out of repo:** the control-plane contract — `OfficeGateway`, `OfficeControlMessage`,
> `HeadquartersControlMessage`, `WorkloadAssignment`, `ClaimOfficeRequest`/`Response`, the certificate
> request/response/challenge/recovery types, `OfficeHeartbeatRequest`, `OfficeSecurityPostureReport`,
> `AssignmentEnvelope`, `OfficeCertificateRecoveryProof`, `LengthDelimitedProtobuf`, and the guest envelope
> types — all live in the `CSweet.Office.Contracts` package. The Headquarters side of every endpoint above is
> a separate repository.

## Related tests

| Test class | What it pins |
|---|---|
| `OfficeTests` | Node version advertising, development-posture consent, the branded local endpoint, single-use request authentication, contract round-trip through the mapper, durable drain and assignment markers, enrollment-token deletion only after state is saved, and the artifact download commit path. |
| `OfficeCertificateRecoveryTests` | That an expired or superseded certificate recovers with key proof and persists the replacement, and that a rejected certificate never overwrites the durable identity (`SEC-INV-02`). |
| `OfficeCertificateTlsTests` | That new TLS connections use the renewed certificate and still validate the server pin (`SEC-INV-04`, `SEC-INV-05`). |
| `ControlPlaneServerCertificateValidatorTests` | A publicly trusted certificate needs no pin; a matching pin accepts a private chain; a pin does not override a hostname mismatch. |
| `OfficeMaintenanceStateTests` | Drain-state durability and assignment-marker lifecycle across process sessions. |
| `OfficeWorkerFailureTests` | That a bounded, actionable broker failure is preserved and an unexpected exception message is not exposed. |

## Related documentation

- [20-security/01-identity-and-enrollment.md](../../20-security/01-identity-and-enrollment.md) and
  [20-security/02-certificate-lifecycle.md](../../20-security/02-certificate-lifecycle.md).
- [20-security/03-headquarters-trust-pinning.md](../../20-security/03-headquarters-trust-pinning.md) — the
  pin file the probe CLI modes read and write.
- [20-security/04-workload-authorization.md](../../20-security/04-workload-authorization.md) — the envelope
  the worker validates.
- [30-workloads/01-assignment-and-lease-semantics.md](../../30-workloads/01-assignment-and-lease-semantics.md),
  [30-workloads/02-runtime-workload-lifecycle.md](../../30-workloads/02-runtime-workload-lifecycle.md), and
  [30-workloads/05-artifacts-and-media.md](../../30-workloads/05-artifacts-and-media.md).
- [10-system/06-process-network-and-storage-surface.md](../../10-system/06-process-network-and-storage-surface.md)
  — the sockets, files, and directories this service owns.
- [../configuration.md](../configuration.md), [../status-and-error-codes.md](../status-and-error-codes.md),
  [../file-layout.md](../file-layout.md).

## Sources

`src/CSweet.Office.Node/CSweet.Office.Node.csproj`, `src/CSweet.Office.Node/Program.cs`,
`src/CSweet.Office.Node/{OfficeWorker,OfficeOptions,OfficeStateStore,OfficeCertificateLease,ControlPlaneServerCertificateValidator,ControlPlaneCertificateProbe,OfficeArtifactCache,ProviderInventory,HostPlatformProvider}.cs`,
`tests/CSweet.Office.Tests/{OfficeTests,OfficeCertificateRecoveryTests,OfficeCertificateTlsTests,ControlPlaneServerCertificateValidatorTests,OfficeMaintenanceStateTests,OfficeWorkerFailureTests}.cs`.

Verified: 2026-09-15.
