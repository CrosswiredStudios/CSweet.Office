# CSweet.Office.Runtime.LocalRpc

The complete Node↔RuntimeHost boundary in one library: the client half that the unprivileged Node uses
(`RuntimeHostProviderClient`), the server half that the privileged service runs (`RuntimeHostRpcServer`,
`RuntimeHostRequestDispatcher`), the transport, the contracts↔protocol mapper, and the authorization gate
that is the only code allowed to turn a Headquarters assignment into a workload. It sits above
`Runtime.Abstractions` and `Runtime.Protocol` and is referenced by both services.

## Project facts

| Fact | Value |
|---|---|
| Output kind | library (`Microsoft.NET.Sdk`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `CSweet.Office.Runtime.Abstractions`, `CSweet.Office.Runtime.Protocol` |
| Key package references | `Microsoft.Extensions.Logging.Abstractions` |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.Runtime.LocalRpc`) |
| `InternalsVisibleTo` | not set |
| csproj `<Description>` | absent |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `LocalRuntimeHostTransport` | static class | `ConnectAsync(RuntimeHostEndpointOptions)`: a named pipe on Windows, a Unix domain socket elsewhere, with the configured connect timeout applied. |
| `RuntimeHostEndpointOptions` | class | `SectionName = "CSweet:Office:RuntimeHost"`: `NamedPipeName` (`csweet-office-runtime-v1`), `AllowedClientSid`, `AllowedClientSids`, `UnixSocketPath` (`/run/csweet/csweet-office-runtime-v1.sock`), `ConnectTimeoutSeconds` (2), `MaximumFrameBytes` (1 MiB), plus `Validate()`. |
| `RuntimeHostRpcServer` | class | Owns the listener. On Windows it creates a security descriptor per pipe instance; on POSIX it binds a Unix socket and sets mode `0660`. Handles exactly one request per connection. |
| `RuntimeHostRequestDispatcher` | class | Routes a validated envelope: pin trust, probe, create, start, inspect, stop, destroy, read logs. Returns an async stream of response envelopes. |
| `RuntimeHostProtocolMapper` | static class | Converts between `CSweet.Office.Contracts.Workloads` types and the protobuf messages, with all bounds re-checked in both directions. |
| `RuntimeHostProviderClient` | class | The Node-side `IRuntimeHostClient`: probe with retry, `CreateAuthorizedAsync`, lifecycle calls, log streaming, `OpenGuestChannelAsync`, `PinHeadquartersTrustAsync`. |
| `RuntimeHostAuthorizationGate` | class | The privileged authorization boundary: the trust pin, the fencing-epoch ledger, and the authorized-handle ledger. |
| `RuntimeHostAuthorizationOptions` | class | `SectionName = "CSweet:Office:RuntimeHost:Authorization"`: `StateDirectory`, `MaximumAuthorizationLifetimeSeconds` (600), `MaximumClockSkewSeconds` (120), plus `ResolveStateDirectory()`, which defaults to `%PROGRAMDATA%\CSweet\Office\authorization`. |

### The three ledgers in `RuntimeHostAuthorizationGate`

| File | Backing method | Written by | Purpose |
|---|---|---|---|
| `headquarters-trust.json` | `Pin` / `ReadTrust` | `Pin` only | The write-once Headquarters assignment trust: office id, key id, ECDSA subject-public-key-info bytes. |
| `accepted-assignments.json` | `ValidateAndCommit` / `ReadReplayState` | `ValidateAndCommit` | Assignment id → highest accepted fencing epoch. Never pruned. |
| `authorized-workload-handles.json` | `RegisterHandle` / `ReadHandles` / `RemoveHandle` | `RegisterHandle`, `RemoveHandle` | Workload id → provider, instance id, kind, assignment id, fencing epoch, lease expiry. |

## Entry points / composition

The library has no `Program.cs`; it composes into both services:

- `CSweet.Office.Node.Program.cs` registers `RuntimeHostEndpointOptions`, `RuntimeHostAuthenticationOptions`
  (loading the shared key from `%PROGRAMDATA%\CSweet\Office\runtime-host.key` when no key is configured),
  `RuntimeHostRequestAuthenticator`, and one `RuntimeHostProviderClient` built by a local `CreateClient`
  helper from the `HostPlatformProvider.Resolve()` descriptor.
- `CSweet.Office.RuntimeHost.Program.cs` registers `RuntimeHostAuthorizationOptions`,
  `RuntimeHostAuthorizationGate`, `RuntimeHostRequestDispatcher`, and `RuntimeHostRpcServer`, then
  `RuntimeHostWorker` runs `server.RunAsync(stoppingToken)` for the process lifetime.

## Behaviour worth knowing

This project is security-critical. Treat every change here as a change to the trust boundary.

- **One request per connection, and no response for a rejected frame.** `HandleOwnedStreamAsync` reads one
  envelope, checks the protocol version, validates authentication, dispatches, signs each response, and closes
  the connection. Length, version, and authentication failures are logged and dropped without writing a byte
  ([SEC-INV-13](../../20-security/11-security-invariants.md)).
- **The guest-channel request is special-cased** in the server, not the dispatcher: after the signed
  `OpenGuestChannelResponse` frame, the server splices the client stream to the provider connector's stream
  with two 64 KiB copies until either direction ends.
- **Windows pipe ACLs are exact.** `CreateWindowsPipeSecurityForClients` calls
  `SetAccessRuleProtection(isProtected: true, preserveInheritance: false)`, grants `FullControl` to the service
  identity, `WellKnownSidType.LocalSystemSid`, and `WellKnownSidType.BuiltinAdministratorsSid`, and grants each allowed client SID only
  `ReadWrite | Synchronize | CreateNewInstance`, with a comment recording that Windows maps duplex
  generic-write opens to `FILE_CREATE_PIPE_INSTANCE`
  ([SEC-INV-12](../../20-security/11-security-invariants.md)).
- **Unix sockets are created fresh.** `RunUnixSocketAsync` deletes an existing path, binds, sets `0660`, listens
  with a backlog of 128, and deletes the path on shutdown.
- **The dispatcher refuses to serve a provider that has no guest-channel connector.** A probe that would
  otherwise succeed is rewritten to unavailable with certification cleared, and a create request is rejected
  with `guest-channel-unavailable`. A provider that cannot hand back the broker stream cannot run a workload.
- **`ValidateAndCommit` is the only entry to workload creation**, and its order is trust → ids → window →
  digest → signature → commit ([SEC-INV-08](../../20-security/11-security-invariants.md)). The commit step
  rejects `previousEpoch >= authorization.FencingEpoch`, so a replay never reaches a backend
  ([SEC-INV-09](../../20-security/11-security-invariants.md)). `Pin` is write-once with the single permitted
  `Guid.Empty → real OfficeId` upgrade ([SEC-INV-01](../../20-security/11-security-invariants.md)).
- **Handles are re-checked on every operation.** `IsHandleAuthorized` requires provider id, provider instance
  id, and workload kind to match the ledger and, unless the caller asked for a termination path, requires the
  lease to be unexpired ([SEC-INV-15](../../20-security/11-security-invariants.md)). `Destroy` is the only
  operation that removes a handle entry.
- **Handle persistence failure destroys the workload.** If `RegisterHandle` throws after `backend.CreateAsync`
  returned, the dispatcher destroys the just-created workload before surfacing the failure.
- **`RuntimeHostProviderClient.CreateAsync` throws unconditionally** with
  `IsolationUnavailableException("RuntimeHost creation requires a signed Headquarters workload authorization.")`
  ([SEC-INV-07](../../20-security/11-security-invariants.md)). There is no unsigned create path.
- **Probe retries, everything else does not.** `ProbeAsync` gets four attempts with 250 ms × attempt backoff
  and only for transient `IOException`, `TimeoutException`, or cancellation not requested by the caller; every
  other operation is single-shot.
- **Client-side response validation is symmetric.** The client re-checks authentication, protocol version,
  request-id echo, and expected body arm before trusting a response
  ([SEC-INV-14](../../20-security/11-security-invariants.md)).
- **Error codes are typed strings, not exceptions, on the wire.** Dispatcher codes include
  `unsupported-operation`, `headquarters-trust-rejected`, `provider-not-registered`,
  `guest-channel-unavailable`, `authorization-rejected`, `provider-unavailable`, `invalid-workload`,
  `provider-create-failed`, `invalid-grace-period`, `workload-not-found`, `provider-operation-failed`, and
  `provider-inspect-failed`.
- **All bounds are re-validated in the mapper.** Digests must be lowercase `sha256:`; the broker lease must be
  bound to the same guest image and artifact digest as the workload; repository URLs must be credential-free
  HTTPS; commit shas must be 40 hex characters; entrypoints are limited to 32 items of at most 1024
  characters; toolchain dependency hosts are limited to 32 and must be valid host names.
- **Failure diagnostics are correlated, not leaked.** Provider failures return a generic message plus the
  request id so an operator can join the Node and RuntimeHost logs; the dispatcher logs the exception locally.

## Related tests

| Test class | What it pins |
|---|---|
| `RuntimeHostRpcIntegrationTests` | The largest test class in the repository. Grants exact duplex rights on the Windows pipe; authenticates and dispatches a typed lifecycle over a real connection; returns a correlated typed error instead of closing the pipe; preserves a sanitized provider diagnostic; rejects signed-authorization replay and provider substitution; rejects tampering, expiry, and trust replacement; rejects forged and expired provider handles. |
| `RuntimeHostAuthenticationTests` | Signature, skew, and replay rules that the server depends on. |
| `RuntimeHostProtocolMapperTests` | Round-trip fidelity of a bounded runtime spec, rejection of a mismatched artifact binding, and rejection of credentials in a repository URL. |
| `OfficeTests` | Round-trips the shared contract through the mapper and asserts single-use authentication at the Office level. |
| `PlatformIsolationBackendTests` | Confirms that the dispatcher's fail-closed probe path has no provider to hide behind. |

## Related documentation

- [20-security/04-workload-authorization.md](../../20-security/04-workload-authorization.md) — the gate, the
  fencing ledger, and the handle ledger.
- [20-security/05-local-rpc-boundary.md](../../20-security/05-local-rpc-boundary.md) — transport, ACLs, and
  framing.
- [20-security/03-headquarters-trust-pinning.md](../../20-security/03-headquarters-trust-pinning.md) — the
  write-once pin.
- [30-workloads/01-assignment-and-lease-semantics.md](../../30-workloads/01-assignment-and-lease-semantics.md)
  and [30-workloads/02-runtime-workload-lifecycle.md](../../30-workloads/02-runtime-workload-lifecycle.md) —
  how the client calls are sequenced.
- [10-system/03-components.md](../../10-system/03-components.md) — the `RuntimeHostProviderClient` seam.
- [../wire-protocols.md](../wire-protocols.md) and [../status-and-error-codes.md](../status-and-error-codes.md).

## Sources

`src/CSweet.Office.Runtime.LocalRpc/CSweet.Office.Runtime.LocalRpc.csproj`,
`src/CSweet.Office.Runtime.LocalRpc/{LocalRuntimeHostTransport,RuntimeHostEndpointOptions,RuntimeHostRpcServer,RuntimeHostRequestDispatcher,RuntimeHostProtocolMapper,RuntimeHostProviderClient,RuntimeHostAuthorizationGate}.cs`,
`src/CSweet.Office.Node/Program.cs`, `src/CSweet.Office.RuntimeHost/Program.cs`,
`tests/CSweet.Office.Tests/{RuntimeHostRpcIntegrationTests,RuntimeHostAuthenticationTests,RuntimeHostProtocolMapperTests}.cs`.

Verified: 2026-09-15.
