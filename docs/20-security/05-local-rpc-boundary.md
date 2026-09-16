# Local RPC boundary

**Audience:** security reviewers; contributors to `Runtime.Protocol`, `Runtime.LocalRpc`, `RuntimeHost`, or
`Node`.

The Node is unprivileged; the RuntimeHost is privileged. This boundary is where that transition happens, and
it is authenticated in both directions.

## Transport

Not gRPC. The proto file is compiled with `GrpcServices="None"` and the frames are length-delimited
protobuf written directly to a stream.

| Platform | Transport | Name | Protection |
|---|---|---|---|
| Windows | `NamedPipeClientStream` / `NamedPipeServerStream` | `csweet-office-runtime-v1` | Explicit DACL — see below |
| Linux, macOS | `AF_UNIX` stream socket | `/run/csweet/csweet-office-runtime-v1.sock` | Mode `0660` |

Framing comes from `LengthDelimitedProtobuf` in the contracts package: a 4-byte big-endian length prefix,
`DefaultMaximumFrameBytes = 1 MiB`, `AbsoluteMaximumFrameBytes = 16 MiB`.

**One request per connection.** The server handles a stream, produces a response, and the connection ends —
except for `OpenGuestChannel`, which converts the same connection into a raw byte tunnel.

`RuntimeHostEndpointOptions` validation:

| Property | Default | Constraint |
|---|---|---|
| `NamedPipeName` | `csweet-office-runtime-v1` | ≤ 100 characters, ASCII alphanumeric plus `-` and `_` only |
| `UnixSocketPath` | `/run/csweet/csweet-office-runtime-v1.sock` | absolute, ≤ 200 characters, no `.` or `..` segments, no NUL |
| `ConnectTimeoutSeconds` | 2 (installer sets 10) | 1–120 |
| `MaximumFrameBytes` | 1 MiB | 4096–16 MiB |

## The authenticated envelope

`RuntimeHostEnvelope` carries `protocol_version`, `request_id`, `authentication_key_id`,
`authentication_timestamp_unix_seconds`, `authentication_nonce`, `authentication_signature`, and a `oneof`
body.

`RuntimeHostRequestAuthenticator` implements HMAC-SHA256 with these properties:

- **Canonicalization.** The envelope is cloned, `AuthenticationSignature` is **cleared**, and the clone is
  serialized with the generated protobuf code. The HMAC is computed over those bytes.
- **Signing** sets the key id, a seconds timestamp, a 32-byte random nonce
  (`RandomNumberGenerator.GetBytes(32)`), then the 32-byte HMAC.
- **Nonce and signature lengths are exact.** A nonce that is not 32 bytes, or a signature that is not
  32 bytes, is rejected as `invalid-authentication-envelope`.
- **Key material** is either `SharedKeyBase64` or a file (`SharedKeyFilePath`). `ParseKey` requires valid
  Base64 and a **minimum of 32 bytes** after decoding.

> **Canonicalization assumption.** Correctness depends on deterministic serialization of the generated
> messages. Adding map fields or changing proto options could silently break interoperability between the
> two sides. Treat the proto as a frozen wire format.

## Validation order and rejection codes

| Order | Code | Condition |
|---|---|---|
| 1 | `unknown-key` | No key matches `AuthenticationKeyId` |
| 2 | `invalid-authentication-envelope` | Nonce ≠ 32 bytes or signature ≠ 32 bytes |
| 3 | `invalid-timestamp` | Timestamp cannot be parsed |
| 4 | `expired-request` | The absolute difference between now and the timestamp exceeds the clamped skew (default 60 s, clamped 1–300) |
| 5 | `invalid-signature` | `CryptographicOperations.FixedTimeEquals` fails |
| 6 | `replayed-request` | The nonce is already in the replay cache |

The signature is verified **before** the nonce is consumed. Reversing that order would let an unsigned
request burn a nonce — a cheap denial of service against a legitimate caller.

`_nonces.TryAdd(hexNonce, now + clamp(ReplayRetentionSeconds, 60, 3600))`, default retention 300 seconds.
`Prune(now)` runs at the start of every validation.

Related invariants: `SEC-INV-13`.

## Responses are authenticated too

The server signs every response with a fresh nonce
(`RuntimeHostRpcServer.HandleOwnedStreamAsync`), and the client rejects a response that fails validation.
`RuntimeHostProviderClient.ValidateResponse` checks, in order:

1. response authentication,
2. `ProtocolVersion == "1.0"`,
3. the request-id echo,
4. the body case.

Without this, a local attacker who could write to the pipe could inject forged handles or statuses into the
Node. In practice the pipe DACL already prevents that; the response signature prevents it even if the ACL is
misconfigured.

Related invariant: `SEC-INV-14`.

## The no-oracle rule

`RuntimeHostRpcServer` gates each frame in order: frame-length limit → protocol version → authentication →
dispatch. A failure at any of the first three stages logs and **closes the connection with no response**.

There is deliberately no distinguishable error for "wrong protocol" versus "bad signature" versus "replayed
nonce" on the wire. The reason strings exist only in the server's log.

## Transport ACLs

### Windows named pipe

`CreateWindowsPipeSecurityForClients` applies:

- `SetAccessRuleProtection(isProtected: true, preserveInheritance: false)` — inherited ACEs are discarded.
- Full control for the server's own identity, `LocalSystem`, and `BuiltinAdministrators`.
- Each configured client SID (`AllowedClientSids` ∪ `AllowedClientSid`) receives exactly
  `ReadWrite | Synchronize | CreateNewInstance`.

The `CreateNewInstance` right is required because Windows maps duplex generic-write opens to
`FILE_CREATE_PIPE_INSTANCE` (`0x4`). Regression test
`WindowsPipeSecurity_GrantsExactDuplexClientConnectionRights` pins this exact set.

The installer configures `AllowedClientSid = <ControlPlaneUserSid>` and
`AllowedClientSids = [<ControlPlaneUserSid>, <nodeServiceSid>]`.

Related invariant: `SEC-INV-12`.

### Unix domain socket

The server deletes a stale path, binds, applies `File.SetUnixFileMode(path, UserRead | UserWrite | GroupRead | GroupWrite)`
(that is `0660`), calls `Listen(128)`, and removes the socket on shutdown.

## Shared key provisioning

| Platform | Path | Protection |
|---|---|---|
| Windows | `%ProgramData%\CSweet\Office\runtime-host.key` | 32 random bytes, Base64; `icacls /inheritance:r` with `R` for the control-plane user, the Node SID, and the RuntimeHost SID, and full control for `SYSTEM` and `Administrators` |
| Linux | `/var/lib/csweet/office/runtime-host.key` | `dd if=/dev/urandom bs=32 count=1 \| base64`; `chown root:csweet-runtime`, `chmod 0640`; the Node unit adds `SupplementaryGroups=csweet-runtime` |
| macOS | via `install-office.sh` and the launchd plists | same pattern |

`RuntimeHostAuthenticationOptions` resolves the key as follows:

- `LoadSharedKeyFileIfNeeded(defaultPath)` reads it once at startup.
- `ResolveSharedKeyBase64()` **re-reads on every call**, so a key created or rotated after startup is picked
  up without a restart. Regression test `Authenticator_LoadsKeyCreatedAfterControlPlaneStartup` pins this.
- `ReadSharedKeyFile` requires the file to exist, be 40–4096 bytes, and **not** carry
  `FileAttributes.ReparsePoint`.

> **Blast radius.** The key is readable by the interactive control-plane user as well as both service SIDs.
> That user can authenticate local RPC calls but still cannot create a workload, because a Headquarters
> signature is required. This is intentional defence in depth, not an oversight. See
> [12-known-limits-and-tradeoffs.md](12-known-limits-and-tradeoffs.md).

## Server dispatch rules

`RuntimeHostRequestDispatcher` enforces:

- A **provider allow-list** built from dependency injection, keyed by `Descriptor.ProviderId` with ordinal
  comparison.
- `Probe` marks a provider unavailable — and clears its certification — when no
  `IPlatformGuestChannelConnector` is registered.
- `Create` requires both a backend and a connector, then `ValidateAndCommit`.
- Every other operation resolves through `IsHandleAuthorized`.
- Stop grace periods are bounded to 0–300 seconds.
- Log reads are bounded to 1 byte–1 GiB and report a `Truncated` flag.
- Errors are typed and sanitized, ending with `"Diagnostic request: {requestId}."` so a support engineer can
  correlate without exposing internals.

### Retry policy

`ProbeRequest` is the **only** operation that is retried: 4 attempts with a `250 ms × attempt` delay, and
only for `IOException`, `TimeoutException`, or a non-user cancellation. Every other operation gets exactly
one attempt.

## Sources

`src/CSweet.Office.Runtime.Protocol/{runtime_host.proto,RuntimeHostAuthentication.cs}`,
`src/CSweet.Office.Runtime.LocalRpc/{LocalRuntimeHostTransport.cs,RuntimeHostEndpointOptions.cs,RuntimeHostRpcServer.cs,RuntimeHostRequestDispatcher.cs,RuntimeHostProviderClient.cs}`,
`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`,
`scripts/linux/{install-office.sh,csweet-office-runtime.service}`,
`tests/CSweet.Office.Tests/{RuntimeHostAuthenticationTests.cs,RuntimeHostRpcIntegrationTests.cs}`.

Verified: 2026-09-15.
