# CSweet.Office.Runtime.Protocol

The wire contract for the local Node→RuntimeHost channel and the shared-secret authenticator that protects
it. It is a plain library, not a gRPC service library: the project generates protobuf message types with
`GrpcServices="None"` because the transport is a private named pipe or Unix socket carrying one
length-delimited envelope per connection, not HTTP/2. It sits beside `Runtime.Abstractions` with no project
references and is referenced by `Node`, `RuntimeHost`, `Runtime.LocalRpc`, `RuntimeGuest`, and
`WindowsSmokeTest`.

## Project facts

| Fact | Value |
|---|---|
| Output kind | library (`Microsoft.NET.Sdk`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | none |
| Key package references | `Google.Protobuf`, `Grpc.Core.Api`, `Grpc.Tools` (`PrivateAssets="All"`) |
| Protobuf item | `<Protobuf Include="runtime_host.proto" GrpcServices="None" />` |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.Runtime.Protocol`) |
| `InternalsVisibleTo` | not set |
| csproj `<Description>` | "Private local RPC contracts between the Office node and privileged RuntimeHost." |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `RuntimeHostEnvelope` | generated protobuf message | The single frame type. Either a request or a response; the direction is implied by the `body` arm. |
| `csweet.office.runtime.v1` | proto package | Wire package name; the C# namespace is `CSweet.Office.Runtime.Protocol`. |
| `RuntimeHostAuthenticationOptions` | class | `SectionName = "CSweet:Office:RuntimeHost:Authentication"`; `KeyId` (default `control-plane`), `SharedKeyBase64`, `SharedKeyFilePath`, `MaximumClockSkewSeconds` (60), `ReplayRetentionSeconds` (300), `LoadSharedKeyFileIfNeeded`, `ResolveSharedKeyBase64`. |
| `RuntimeHostRequestAuthenticator` | class | `Sign(RuntimeHostEnvelope)` and `Validate(RuntimeHostEnvelope)` using HMAC-SHA256 over the canonical envelope. |
| `RuntimeHostAuthenticationResult` | record | `(bool Accepted, string? ErrorCode)`, with a `Reject(code)` factory and `Accept()`. |

## Envelope fields

| Field | Type | Notes |
|---|---|---|
| `protocol_version` | string | Callers set `"1.0"`; the server closes any connection whose version is not `1.0`. |
| `request_id` | string | A GUID in `N` format; echoed on responses so a client can correlate. |
| `authentication_key_id` | string | Must equal `RuntimeHostAuthenticationOptions.KeyId`. |
| `authentication_timestamp_unix_seconds` | int64 | Checked against clock skew before the signature. |
| `authentication_nonce` | bytes | Exactly 32 random bytes. |
| `authentication_signature` | bytes | Exactly 32 bytes of HMAC-SHA256; cleared while the signature is computed. |
| `body` | oneof | Sixteen arms: `probe_request`, `probe_response`, `create_request`, `create_response`, `start_request`, `inspect_request`, `inspect_response`, `stop_request`, `destroy_request`, `operation_response`, `read_logs_request`, `log_chunk`, `open_guest_channel_request`, `open_guest_channel_response`, `pin_headquarters_trust_request`, `pin_headquarters_trust_response`. |

The supporting messages are `PinHeadquartersTrustRequest` (office id, key id, verification public key),
`OpenGuestChannelRequest` / `OpenGuestChannelResponse`, `ProbeRequest` / `ProbeResponse`,
`CreateWorkloadRequest`, `SignedWorkloadAuthorization`, `ResourceLimits`, `BrokerLease`, `BuilderSpec`,
`RuntimeSpec`, `ToolchainBuildSpec`, `WorkloadOperationRequest`, `StopWorkloadRequest`,
`WorkloadHandleResponse`, `WorkloadStatusResponse`, `ReadLogsRequest`, and `LogChunk`.

## Entry points / composition

The library is used from `RuntimeHostProviderClient` (sign request, validate response) and
`RuntimeHostRpcServer` (validate request, sign every response). Both call
`LengthDelimitedProtobuf.WriteAsync` / `ReadAsync` from `CSweet.Office.Contracts` for framing and
`RuntimeHostRequestAuthenticator` for authentication, so the wire format is one 4-byte big-endian length
prefix followed by the serialized envelope.

## Behaviour worth knowing

- **`GrpcServices="None"` is deliberate.** The proto generates messages only; there is no generated client
  or server. The transport is `LocalRuntimeHostTransport`, and the service is hand-rolled in
  `RuntimeHostRpcServer`. Do not switch this to `GrpcServices="Both"` without replacing the transport.
- **Validation order is fixed**: key id, then envelope shape (nonce and signature lengths), then timestamp
  parse, then clock skew, then signature, then nonce replay. Rejection codes, in that order, are
  `unknown-key`, `invalid-authentication-envelope`, `invalid-timestamp`, `expired-request`,
  `invalid-signature`, `replayed-request`.
- **Signature comparison is constant-time** (`CryptographicOperations.FixedTimeEquals`), and the nonce is
  only consumed after the signature matches, so unsigned traffic cannot burn nonces
  ([SEC-INV-13](../../20-security/11-security-invariants.md)).
- **Clamps are enforced in code**: clock skew is clamped to 1-300 s and replay retention to 60-3600 s when
  used, regardless of configured values ([SEC-INV-11](../../20-security/11-security-invariants.md)).
- **The shared key file is read defensively.** A key file must be a regular file of 40-4096 bytes, must not be
  a reparse point, and read failures are swallowed into an empty key — which then fails `ParseKey`. There is no
  path that treats a missing key as "no authentication".
- **The replay cache is in-memory.** Nonces live in a `ConcurrentDictionary` and are pruned lazily on each
  validation. Restarting RuntimeHost forgets the cache; see
  [20-security/12-known-limits-and-tradeoffs.md](../../20-security/12-known-limits-and-tradeoffs.md).

## Related tests

| Test class | What it pins |
|---|---|
| `RuntimeHostAuthenticationTests` | A signed envelope is accepted once; a changed body, an expired envelope, a replayed envelope, and a foreign key are rejected. |
| `RuntimeHostRpcIntegrationTests` | End-to-end use of the envelope over a real pipe, including that unauthenticated frames get no response and that responses are signed and request-id-bound. |
| `OfficeTests` | Includes a direct assertion that the authenticator accepts a signed envelope only once. |

## Related documentation

- [20-security/05-local-rpc-boundary.md](../../20-security/05-local-rpc-boundary.md) — framing, HMAC, nonces,
  and ACLs.
- [20-security/04-workload-authorization.md](../../20-security/04-workload-authorization.md) — the signed
  authorization envelope carried inside `CreateWorkloadRequest`.
- [20-security/11-security-invariants.md](../../20-security/11-security-invariants.md) — `SEC-INV-11`,
  `SEC-INV-13`, `SEC-INV-14`.
- [../wire-protocols.md](../wire-protocols.md) — the arm-by-arm reference.
- [runtime-localrpc.md](runtime-localrpc.md) — the client and server that use these types.

## Sources

`src/CSweet.Office.Runtime.Protocol/CSweet.Office.Runtime.Protocol.csproj`,
`src/CSweet.Office.Runtime.Protocol/runtime_host.proto`,
`src/CSweet.Office.Runtime.Protocol/RuntimeHostAuthentication.cs`,
`src/CSweet.Office.Runtime.LocalRpc/{RuntimeHostProviderClient,RuntimeHostRpcServer}.cs`,
`tests/CSweet.Office.Tests/{RuntimeHostAuthenticationTests,RuntimeHostRpcIntegrationTests}.cs`.

Verified: 2026-09-15.
