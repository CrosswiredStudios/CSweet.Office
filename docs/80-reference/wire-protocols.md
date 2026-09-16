# Wire protocols

**Audience:** contributors changing a message, a field number, or a stream shape; reviewers checking that a
change stays inside a signed boundary.

Three surfaces carry structured messages. Each has its own envelope, its own version string, and its own
transport, and none of them is interchangeable with the others.

| Surface | Between | Definition | Transport |
|---|---|---|---|
| Local RPC | Node ↔ RuntimeHost | `src/CSweet.Office.Runtime.Protocol/runtime_host.proto` | Windows named pipe or Unix socket, one request per connection. |
| Control plane | Node ↔ Headquarters | `CSweet.Office.Contracts` (`ControlPlane/office_control.proto`, `ControlPlane/EnrollmentContracts.cs`) | HTTPS JSON and gRPC over HTTP/2. |
| Guest broker | RuntimeHost ↔ in-guest broker | `CSweet.Office.Contracts` (`Guest/guest_broker.proto`) | vSock (`AF_HYPERV`, `AF_VSOCK`) or the helper's stdio guest channel. |

All three use the same length-delimited protobuf framing implementation (part 1).

---

# Part 1 — Local RPC

## Definition and code generation

| Fact | Value |
|---|---|
| Proto package | `csweet.office.runtime.v1` |
| C# namespace | `CSweet.Office.Runtime.Protocol` |
| Proto file | `src/CSweet.Office.Runtime.Protocol/runtime_host.proto` |
| Code generation | `<Protobuf Include="runtime_host.proto" GrpcServices="None" />` in `src/CSweet.Office.Runtime.Protocol/CSweet.Office.Runtime.Protocol.csproj` |
| Server | `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostRpcServer.cs` |
| Client | `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostProviderClient.cs` |
| Dispatcher | `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostRequestDispatcher.cs` |

`GrpcServices="None"` is deliberate: the messages are protobuf, the transport is not gRPC. There is no HTTP/2
service, no method name on the wire, and no reflection service. The client sends one `RuntimeHostEnvelope`,
the server replies with one or more `RuntimeHostEnvelope` frames, and the connection is closed.

## Framing

`LengthDelimitedProtobuf` writes a 4-byte **big-endian** length prefix followed by the serialized message, and
flushes after every frame.

| Property | Value |
|---|---|
| Prefix | `BinaryPrimitives.WriteInt32BigEndian` of `message.CalculateSize()` |
| Message limit | `RuntimeHostEndpointOptions.MaximumFrameBytes` (default `1048576`, permitted 4096–16 MiB) |
| Absolute limit | `LengthDelimitedProtobuf.AbsoluteMaximumFrameBytes` = `16 * 1024 * 1024` |
| Default | `LengthDelimitedProtobuf.DefaultMaximumFrameBytes` = `1024 * 1024` |
| Oversize on write | `InvalidDataException("The protobuf frame exceeds the <n>-byte limit.")` |
| Bad length on read | `InvalidDataException("The protobuf frame length <n> is outside the allowed range.")` |
| Truncated prefix or payload | `EndOfStreamException` |
| Connection close before a request | `ReadAsync` returns `null`; the server logs and closes silently |

The reader is deliberately lenient about a zero-length prefix but not about a negative or oversized one: a
closed connection before the first frame is a normal client disconnect, whereas a malformed length is a
protocol error.

## `RuntimeHostEnvelope`

| Field | Number | Type | Purpose |
|---|---|---|---|
| `protocol_version` | 1 | `string` | Must be `1.0`; any other value is rejected before authentication and the connection is closed with no response. |
| `request_id` | 2 | `string` | Echoed in every response and covered by the signature. |
| `authentication_key_id` | 3 | `string` | Must equal `RuntimeHostAuthenticationOptions.KeyId`. |
| `authentication_timestamp_unix_seconds` | 4 | `int64` | Unixtime seconds; skew limited by `MaximumClockSkewSeconds`. |
| `authentication_nonce` | 5 | `bytes` | Exactly 32 bytes. |
| `authentication_signature` | 6 | `bytes` | Exactly 32 bytes; HMAC-SHA-256 over the envelope with this field cleared. |

### The 16 `body` arms

Every arm is listed. The dispatcher decides on `BodyCase`; an unhandled arm is not possible because the
switch is exhaustive over the arms the dispatcher implements, and anything else falls to the
`unsupported-operation` response.

| # | Field number | Arm | Message | Carries |
|---|---|---|---|---|
| 1 | 10 | `probe_request` | `ProbeRequest` | `provider_id` |
| 2 | 11 | `probe_response` | `ProbeResponse` | `provider_id`, `provider_version`, `host_operating_system`, `host_architecture`, `assurance`, `available`, `unavailable_reason`, `certification_json` (serialized `IsolationProviderCertification`, empty when unavailable) |
| 3 | 12 | `create_request` | `CreateWorkloadRequest` | `provider_id`, `workload_id`, `workload_kind`, `guest_image_id`, `guest_image_version`, `guest_image_digest`, `guest_operating_system`, `guest_architecture`, `resource_limits`, `broker_lease`, `builder`, `runtime`, `toolchain_build`, `authorization` |
| 4 | 13 | `create_response` | `WorkloadHandleResponse` | `success`, `error_code`, `sanitized_error`, `workload` (`WorkloadOperationRequest`) |
| 5 | 14 | `start_request` | `WorkloadOperationRequest` | `provider_id`, `workload_id`, `provider_instance_id`, `workload_kind` |
| 6 | 15 | `inspect_request` | `WorkloadOperationRequest` | same four fields |
| 7 | 16 | `inspect_response` | `WorkloadStatusResponse` | `found`, `workload`, `state`, `termination_reason`, `exit_code` (optional), `started_at_unix_milliseconds`, `finished_at_unix_milliseconds`, `error_code`, `sanitized_error` |
| 8 | 17 | `stop_request` | `StopWorkloadRequest` | `workload` (`WorkloadOperationRequest`), `grace_period_seconds` |
| 9 | 18 | `destroy_request` | `WorkloadOperationRequest` | same four fields; the only operation that removes the authorized handle |
| 10 | 19 | `operation_response` | `OperationResponse` | `success`, `error_code`, `sanitized_error` (used by start, stop, destroy, and the trust pin) |
| 11 | 20 | `read_logs_request` | `ReadLogsRequest` | `workload`, `maximum_bytes` |
| 12 | 21 | `log_chunk` | `LogChunk` | `occurred_at_unix_milliseconds`, `stream`, `content`, `truncated`, `completed`; one frame per chunk plus a final `completed = true` frame |
| 13 | 22 | `open_guest_channel_request` | `OpenGuestChannelRequest` | `workload` (`WorkloadOperationRequest`) |
| 14 | 23 | `open_guest_channel_response` | `OpenGuestChannelResponse` | `success`, `error_code`, `sanitized_error` |
| 15 | 24 | `pin_headquarters_trust_request` | `PinHeadquartersTrustRequest` | `office_id`, `assignment_signing_key_id`, `assignment_verification_public_key` |
| 16 | 25 | `pin_headquarters_trust_response` | `OperationResponse` | `success`, `error_code`, `sanitized_error` |

Supporting messages carried inside those arms: `SignedWorkloadAuthorization` (authorization version, office
id, assignment id, workload id, fencing epoch, provider id, specification JSON, specification SHA-256,
signature key id, signature bytes, issued-at, expires-at), `ResourceLimits` (vCPU, CPU percent, memory,
writable disk, process count, log bytes, maximum duration), `BrokerLease` (channel id, protocol version,
boot token, expected guest image digest, expected artifact digest, expiry), `BuilderSpec`, `RuntimeSpec`, and
`ToolchainBuildSpec`.

## Request lifecycle on the server

1. Read one frame with `MaximumFrameBytes`.
2. Reject a `protocol_version` other than `1.0` — logged, connection closed, **no response** (`SEC-INV-13`).
3. Validate and consume the authentication envelope — logged, connection closed, **no response**.
4. `open_guest_channel_request` is handled by the server itself, not the dispatcher: it checks
   `IsHandleAuthorized`, opens the provider connector, signs and writes the response, and then copies bytes
   in both directions (64 KiB buffers) until either side finishes.
5. Every other arm is dispatched; each yielded response is signed with a fresh nonce and written.
6. The server logs completion with elapsed milliseconds and closes the connection.

The client validates each response: authentication accepted, protocol version match, `request_id` echo, and
the expected body case (`SEC-INV-14`). A mismatch is an `IsolationUnavailableException` on the client side.

## Authentication envelope details

| Item | Value |
|---|---|
| Algorithm | HMAC-SHA-256 over the serialized envelope with `authentication_signature` cleared |
| Key | Base64 shared key, at least 32 bytes, read from `SharedKeyBase64` or re-read from `SharedKeyFilePath` on every signature |
| Nonce | 32 random bytes per signature; the server keeps accepted nonces in memory until their retention expires |
| Order of checks | key id → envelope shape (32-byte nonce and signature) → timestamp range → signature → nonce replay |
| Rejection codes | `unknown-key`, `invalid-authentication-envelope`, `invalid-timestamp`, `expired-request`, `invalid-signature`, `replayed-request` |
| Rejection effect | The code is written to the service log and the connection is closed without a response frame |

The signature is verified **before** the nonce is consumed, so unsigned traffic cannot burn nonces. See
[20-security/05-local-rpc-boundary.md](../20-security/05-local-rpc-boundary.md) and `SEC-INV-13`.

---

# Part 2 — Control plane

> **Out of repo:** every message in this part is defined by `CSweet.Office.Contracts`, and every endpoint is
> served by C-Sweet. The in-repo code is the client: `src/CSweet.Office.Node/{OfficeWorker,ControlPlaneCertificateProbe,ControlPlaneServerCertificateValidator}.cs`,
> `src/CSweet.Office.Node/OfficeArtifactCache.cs`, and `src/CSweet.Office.Configurator/{Program,OfficeMaintenanceService}.cs`.
> The definitions were read from the sibling checkout at `..\CSweet.Office.Contracts`
> (`src/CSweet.Office.Contracts/ControlPlane/{office_control.proto,EnrollmentContracts.cs,OfficeCertificateRecovery.cs}`),
> which is the `CSweetOfficeContractsRepositoryRoot` default. See [cross-repo-contracts.md](cross-repo-contracts.md).

## JSON endpoints

All requests are `application/json`; all but `assignment-trust` carry the Office client certificate once the
Office has an operational certificate.

| Method and path | Request type | Response type | Client file |
|---|---|---|---|
| `POST api/offices/claim` | `ClaimOfficeRequest` | `ClaimOfficeResponse` | `OfficeWorker.EnrollAsync` |
| `POST api/offices/{officeId:D}/heartbeat` | `OfficeHeartbeatRequest` | HTTP status only | `OfficeWorker.RunControlSessionAsync` (bootstrap certificate only) |
| `POST api/offices/{officeId:D}/certificate` | `OfficeCertificateRequest` | `OfficeCertificateResponse` | `OfficeWorker.RefreshOperationalCertificateAsync` |
| `POST api/offices/{officeId:D}/certificate/challenge` | empty body | `OfficeCertificateChallengeResponse` | `OfficeWorker.RecoverOperationalCertificateAsync` |
| `POST api/offices/{officeId:D}/certificate/recover` | `OfficeCertificateRecoveryRequest` | `OfficeCertificateResponse` | `OfficeWorker.RecoverOperationalCertificateAsync` |
| `GET api/offices/assignment-trust` | — | `HeadquartersAssignmentTrustResponse` | `ControlPlaneCertificateProbe.DownloadAssignmentTrustAsync` |

`ClaimOfficeRequest` fields: enrollment token, name, machine name, operating system, architecture, Office
version, protocol version, certificate thumbprint, certificate serial number, certificate expiry, certificate
signing request PEM, allocatable CPU/memory/disk, maximum concurrent workloads, provider inventory
(`RegisterOfficeProviderRequest` per provider), optional `OfficeSecurityPostureReport`, optional assisted
setup session id.

`ClaimOfficeResponse` must contain `Succeeded`, a non-null `OfficeId`, an `EnrollmentReceipt`, an
`AssignmentSigningKeyId`, and an `AssignmentVerificationPublicKeyBase64`; otherwise the Node reports
`Office enrollment failed (<ErrorCode>): <Message>`. The `invalid_enrollment` code is surfaced to the operator
by the Windows installer as *"Generate a new connection code in C-Sweet, then run this installer again."*

`OfficeCertificateRecoveryRequest` is verified server-side against the enrolled key: the client signs
`OfficeCertificateRecoveryProof.Payload(officeId, challenge)`, which is
`"csweet-office-certificate-recovery-v1\n{officeId:D}\n{challenge}"` encoded as UTF-8 and signed with ECDSA
P-256/SHA-256 in IEEE P1363 fixed-field format. The challenge must be a 44-character base64 string (a 32-byte
nonce) that has not expired. The client refuses to send an expired certificate or the bootstrap receipt to
this endpoint, and server certificate validation (including any pin) stays mandatory on both calls
(`SEC-INV-03`).

Assisted-setup and removal endpoints used by the configurator, also C-Sweet-owned:
`api/offices/local-sessions/preflight`, `api/offices/local-sessions/redeem`,
`api/offices/local-sessions/result`, `api/offices/local-sessions/removal-complete`, and
`api/offices/{officeId:D}/maintenance/claim`, with the request and response records
`AssistedOfficePreflightRequest/Response`, `RedeemAssistedOfficeSetupRequest/Response`,
`ReportAssistedOfficeSetupResultRequest`, and `CompleteAssistedOfficeRemovalRequest`.

## `OfficeGateway` gRPC service

`package csweet.office.v1`, C# namespace `CSweet.Office.Contracts.ControlPlane`. The Node is the client for
exactly three RPCs; there are no others.

| RPC | Shape | Purpose |
|---|---|---|
| `Connect(stream OfficeControlMessage) returns (stream HeadquartersControlMessage)` | bidirectional streaming | The control session: enrollment heartbeat, lease renewals, assignment status updates out; assignments, fences, drains, and the gateway hello in. |
| `OpenWorkloadTunnel(stream WorkloadTunnelFrame) returns (stream WorkloadTunnelFrame)` | bidirectional streaming | Relays the raw guest broker byte stream between the guest channel and Headquarters. |
| `DownloadArtifact(ArtifactDownloadRequest) returns (stream ArtifactChunk)` | server streaming | One artifact transfer per assignment-scoped grant. |

The channel is built from a rotating mTLS handler over the current operational certificate, with
`AllowTlsResume = false` and a per-handshake certificate selection callback (`SEC-INV-04`).

### Envelope headers and cases

`OfficeControlMessage` (`protocol_version` 1, `office_id` 2, `session_epoch` 3):

| Case | Field | Message |
|---|---|---|
| `heartbeat` | 10 | `OfficeHeartbeat` |
| `lease_renewal` | 11 | `AssignmentLeaseRenewal` |
| `assignment_status` | 12 | `AssignmentStatusUpdate` |

`HeadquartersControlMessage` (`protocol_version`, `office_id`, `session_epoch`):

| Case | Field | Message | Node behaviour |
|---|---|---|---|
| `assignment` | 10 | `WorkloadAssignment` | Validated, then executed. |
| `fence` | 11 | `FenceAssignment` | Cancels the named in-flight assignment and logs the reason. |
| `drain` | 12 | `DrainOffice` | Persists the drain flag in the Node state store. |
| `hello` | 13 | `GatewayHello` | Must match the pinned signing key id and public key, byte for byte, or the session is terminated. |

Every inbound message is checked against the local `OfficeId` and `SessionEpoch`; a mismatch terminates the
session (`SEC-INV-06`).

### Tunnel frames

`WorkloadTunnelFrame`: `office_id` (1), `assignment_id` (2), `fencing_epoch` (3), `sequence` (4),
`content` (5), `completed` (6), `session_epoch` (7).

The Node writes an explicit empty opening frame with `sequence: 0` before it starts reading from the guest,
because Headquarters cannot start the broker session until it receives a bound frame and the guest waits for
the boot configuration — the three-way deadlock is documented in the comment at that call site. Upload frames
then use `sequence` starting at 1, and the download path requires a strictly increasing sequence starting at 0
with a matching `fencing_epoch`.

### Artifact frames

`ArtifactDownloadRequest`: `protocol_version` (1), `office_id` (2), `session_epoch` (3), `assignment_id` (4),
`fencing_epoch` (5), `artifact_digest` (6), `artifact_read_token` (7), `transfer_id` (8). The `transfer_id` is
generated once per `EnsureAsync` and reused across the three retry attempts.

`ArtifactChunk`: `offset` (1), `content` (2), `completed` (3), `total_size` (4), `sha256` (5). The Node
requires contiguous offsets, refuses any chunk larger than 64 KiB, stops above 2 GiB, requires
`total_size == bytes written`, and re-verifies the SHA-256 over the closed file before committing it. On
`Unavailable`, `Internal`, or `DeadlineExceeded` it retries up to three times with 250 ms × attempt backoff.

---

# Part 3 — Guest surface

> **Out of repo:** the guest envelope, boot configuration, and handshake messages are defined by
> `CSweet.Office.Contracts` in `Guest/guest_broker.proto` and `Guest/GuestHandshake.cs`, and the framing
> helper is `Guest/LengthDelimitedProtobuf.cs` in the same package. The in-repo consumer is
> `src/CSweet.Office.RuntimeGuest/*`. The shapes below were read from the sibling checkout
> `..\CSweet.Office.Contracts`; `src/CSweet.Office.RuntimeGuest/GuestBrokerSession.cs` and
> `src/CSweet.Office.RuntimeGuest/Program.cs` were read in this repository and show how each message is used.

## Framing

The guest broker uses the same length-delimited framing as the local RPC: a 4-byte big-endian length prefix,
`DefaultMaximumFrameBytes` 1 MiB, `AbsoluteMaximumFrameBytes` 16 MiB, and the same exception messages for an
out-of-range length. The effective limit is `min(GuestServiceOptions.MaximumFrameBytes, GuestLease.MaximumFrameBytes)`.

## `GuestEnvelope`

| Field | Number |
|---|---|
| `protocol_version` | 1 |
| `message_id` | 2 |

`message_id` must be a 32-character GUID in `N` format; the guest validates both header fields on every frame
and throws `InvalidDataException("The guest broker envelope is invalid.")` otherwise.

| # | Field | Arm | Message | Direction |
|---|---|---|---|---|
| 1 | 9 | `boot_configuration` | `GuestBootConfiguration` | host → guest |
| 2 | 10 | `hello` | `GuestHello` | guest → host |
| 3 | 11 | `challenge` | `HostChallenge` | host → guest |
| 4 | 12 | `proof` | `GuestProof` | guest → host |
| 5 | 13 | `lease` | `GuestLease` | host → guest |
| 6 | 14 | `health` | `GuestHealth` | both |
| 7 | 15 | `proxy_request` | `ProxyRequest` | guest → host |
| 8 | 16 | `proxy_response` | `ProxyResponse` | host → guest |
| 9 | 17 | `stream_chunk` | `StreamChunk` | both |
| 10 | 18 | `start_command` | `StartCommand` | host → guest |
| 11 | 19 | `shutdown_command` | `ShutdownCommand` | host → guest |
| 12 | 20 | `exit` | `GuestExit` | guest → host |
| 13 | 21 | `boot_failure` | `GuestBootFailure` | guest → host |

## Boot configuration

Written by Headquarters to the guest over the host boot channel; the `stdio` transport reads the same values
from `CSWEET_GUEST_*` environment variables instead.

| Field | Number | Meaning |
|---|---|---|
| `workload_id` | 1 | Workload GUID |
| `channel_id` | 2 | Broker channel GUID |
| `protocol_version` | 3 | Must be `1.0` |
| `guest_image_digest` | 4 | Certified image digest |
| `artifact_digest` | 5 | Optional artifact digest |
| `boot_token` | 6 | At least 16 characters; also the HMAC key and the workload token file contents |
| `lease_expires_at_unix_seconds` | 7 | Must still be in the future |
| `artifact_root` | 8 | For kinds 1 and 2 this must be `/run/csweet/artifact/payload` |
| `workload_kind` | 9 | 0 Builder, 1 Runtime, 2 ToolchainBuild |
| `installation_id` | 10 | Runtime identity; required for kinds 1 and 2 |
| `business_id` | 11 | Runtime identity; must parse as a GUID |
| `tick_id` | 12 | Runtime identity; required for kinds 1 and 2 |
| `local_broker_socket_path` | 13 | Default `/run/csweet/broker.sock` |
| `workload_token_path` | 14 | Default `/run/csweet/workload-token` |
| `maximum_frame_bytes` | 15 | Guest-side frame limit |

A failure while reading, parsing, validating, or materializing the boot configuration is reported back as
`GuestBootFailure` with the reason codes listed in
[status-and-error-codes.md](status-and-error-codes.md#guest-boot-failure-reason-codes).

## Handshake

| Step | Message | Content |
|---|---|---|
| 1 | `GuestHello` | `workload_id`, `channel_id`, `guest_image_digest`, `artifact_digest`, `ephemeral_public_key` (ECDSA P-256 SubjectPublicKeyInfo, 64–1024 bytes), `boot_token_proof` (32 bytes) |
| 2 | `HostChallenge` | `nonce` (32 random bytes), `expires_at_unix_seconds` (at most one minute, and never later than the lease) |
| 3 | `GuestProof` | `signature` — ECDSA P-256/SHA-256 over the challenge payload |
| 4 | `GuestLease` | `accepted`, `reason_code`, `expires_at_unix_seconds`, `maximum_frame_bytes` |

HMAC proof of possession of the boot token (`BootProof`), computed by both sides and compared in constant
time:

```
HMAC-SHA256(key = UTF-8(boot_token), payload =
    "csweet-guest-hello-v1"             + 0x00
  + protocol_version                    + 0x00
  + workload_id (D format)              + 0x00
  + channel_id (D format)               + 0x00
  + guest_image_digest                  + 0x00
  + artifact_digest (possibly empty)    + 0x00
  + lease_expires_at_unix_seconds (invariant) + 0x00
  + ephemeral_public_key bytes)
```

Challenge payload (`ChallengePayload`), signed by the guest:

```
"csweet-guest-challenge-v1"    + 0x00
+ workload_id (D format)       + 0x00
+ channel_id (D format)        + 0x00
+ expires_at_unix_seconds (invariant) + 0x00
+ nonce bytes
```

The host verifies the hello identity (workload id, channel id, image digest, artifact digest), the boot-token
proof, and the public key; the challenge is issued once and consumed once. The guest rejects a lease whose
expiry does not equal its boot-bound expiry verbatim (`"The host lease does not match the boot-bound guest
lease."`), and the local broker socket only starts after the lease is accepted. See `SEC-INV-23`.

Rejection reason codes returned in `GuestLease.reason_code`: `challenge-expired`, `invalid-frame-limit`,
`invalid-guest-proof`.

## Command set after the lease

| Arm | Guest behaviour |
|---|---|
| `start_command` | Requires `workload_kind` to equal the boot configuration, `maximum_log_bytes` in 1 B–1 GiB, and (kind 2) `maximum_output_bytes` in 1 B–10 GiB; starts the supervised process; answers `health: running`; then reports exit and, for kinds 1 and 2, streams diagnostics. |
| `proxy_response` | Matched against the pending request id; an unknown id throws `"The host returned an unknown broker response."` |
| `shutdown_command` | Stops the workload with `min(max(grace_period_seconds, 0), 60)`, answers `exit_code: 0` with the host's reason code, and ends the session. |
| `health` | Answers with `running` when the workload is running, otherwise `ready`. |

Any other arm received as a host command throws
`InvalidDataException("The host sent an unsupported guest command.")`.

## Proxy purposes

The guest maps a local HTTP path to the `purpose` field of `ProxyRequest`. Anything not in this table is
refused with `UnauthorizedAccessException("The local broker endpoint is not available to this workload.")`.

| Workload kind | Path | Purpose |
|---|---|---|
| 1 Runtime, 2 ToolchainBuild | `/mcp` | `mcp.runtime` |
| 2 ToolchainBuild, 0 Builder | `/build/fetch` | `build.fetch` |
| 2 ToolchainBuild, 0 Builder | `/build/artifact` | `build.artifact` |
| 2 ToolchainBuild, 0 Builder | `/build/progress` | `build.progress` |

`ProxyRequest` carries `request_id`, `purpose`, `method`, `path`, a `headers` map, and `body`;
`ProxyResponse` carries `request_id`, `status_code`, a `headers` map, `body`, and `error_code`. `StreamChunk`
carries `stream_id` (`runtime.logs` for guest diagnostics), `sequence`, `content`, `completed`, and `digest`.

The local HTTP listener is `/run/csweet/broker.sock` (mode `0660`, group `csweet-workload`, backlog 16). It
accepts a `Content-Length` body or bounded chunked encoding, rejects a request that sets both
(`InvalidDataException`), strips `Transfer-Encoding`, `Connection`, and `Keep-Alive`, and always answers with
an explicit `Content-Length` and `Connection: close`.

## Exit reporting

`GuestExit` carries `exit_code`, `reason_code`, and a bounded `detail` (control characters removed, last 8 KiB
of the supervisor's diagnostic tail). The guest reports `exit_code: 0` with the shutdown reason on a clean
shutdown, `126` with `workload-start-failed`, and `137` with `resource-limit-exceeded`. See
[status-and-error-codes.md](status-and-error-codes.md#guest-exit-codes).

## Sources

`src/CSweet.Office.Runtime.Protocol/{runtime_host.proto,RuntimeHostAuthentication.cs,CSweet.Office.Runtime.Protocol.csproj}`,
`src/CSweet.Office.Runtime.LocalRpc/{RuntimeHostRpcServer.cs,RuntimeHostRequestDispatcher.cs,RuntimeHostProviderClient.cs,RuntimeHostEndpointOptions.cs}`,
`src/CSweet.Office.Node/{OfficeWorker.cs,OfficeArtifactCache.cs,ControlPlaneCertificateProbe.cs}`,
`src/CSweet.Office.Configurator/{Program.cs,OfficeMaintenanceService.cs}`,
`src/CSweet.Office.RuntimeGuest/{Program.cs,GuestBrokerSession.cs,GuestWorkloadSupervisor.cs,GuestLocalBrokerProxy.cs,GuestServiceOptions.cs}`,
`src/CSweet.Office.RuntimeHost/Program.cs`, `src/CSweet.Office.RuntimeHost/appsettings.json`,
`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`, `scripts/linux/csweet-office-runtime.service`,
`scripts/macos/com.csweet.office.runtime.plist`,
and the sibling contracts checkout `..\CSweet.Office.Contracts\src\CSweet.Office.Contracts\{ControlPlane\office_control.proto,ControlPlane\EnrollmentContracts.cs,ControlPlane\OfficeCertificateRecovery.cs,Guest\guest_broker.proto,Guest\GuestHandshake.cs,Guest\LengthDelimitedProtobuf.cs,ProtocolVersions.cs}`.

Verified: 2026-09-15.
