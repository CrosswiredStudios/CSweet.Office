# Guest broker protocol

**Audience:** contributors to `RuntimeGuest`; reviewers of the relay path.

The guest broker is `CSweet.Office.RuntimeGuest`. It validates boot configuration, materializes artifacts,
performs an authenticated handshake, proxies agent SDK requests, and supervises the workload process.

**The Office never interprets this protocol.** Bytes travel from the guest, through the RuntimeHost guest
channel, through the Node's tunnel, to Headquarters — and back. The Office's only role is framing and
sequencing.

> **Out of repo:** the host half of this protocol — issuing the boot configuration, answering the handshake,
> authorizing purposes, and responding to proxy requests — is implemented in Headquarters. The in-repo
> reference implementation is `src/CSweet.Office.WindowsSmokeTest/CertificationBrokerHost.cs`, which is
> test-only.

## Envelopes and framing

All guest traffic is `GuestEnvelope` messages, length-delimited with a 4-byte big-endian length prefix. The
maximum frame is 1 MiB for the boot configuration, and the negotiated `MaximumFrameBytes` (4 KiB – 16 MiB) for
the session.

## Boot configuration

The host's first message on a host-boot transport must be a `GuestEnvelope` whose `BodyCase` is
`BootConfiguration` and whose `MessageId` parses as a GUID in `"N"` format.

```csharp
record GuestBootConfiguration(
    Guid workload_id, Guid channel_id, string protocol_version,
    string guest_image_digest, string? artifact_digest, string boot_token,
    long lease_expires_at_unix_seconds, string artifact_root, WorkloadKind workload_kind,
    Guid? installation_id, string business_id, Guid? tick_id,
    string local_broker_socket_path, string workload_token_path, int maximum_frame_bytes);
```

Validation rules are in [20-security/07-guest-isolation.md](../20-security/07-guest-isolation.md).

Boot failures on a host channel are reported as a `GuestBootFailure` with a reason code of
`guest-boot-invalid`, `guest-boot-io-failed`, or `guest-boot-failed`, a control-character-stripped detail of at
most 512 characters — and then rethrown, which closes the connection. The Office reports the failure and
destroys the VM.

## Transports

Selected by `CSWEET_GUEST_BROKER_TRANSPORT`:

| Value | Transport | Boot configuration source |
|---|---|---|
| `stdio` | `StandardIoGuestBrokerTransport` | Environment variables (`GuestServiceOptions.FromEnvironment`) |
| `hyperv-vsock` | `LinuxHyperVSocketGuestTransport` on port **2761** | Length-delimited boot configuration |
| `firecracker-vsock` | `LinuxHyperVSocketGuestTransport` on `CSWEET_GUEST_VSOCK_PORT` (default **5000**) | Length-delimited boot configuration |
| anything else | throws | — |

`stdio` exists for the certification harness. Production Linux guests use a host-boot transport.

`LinuxHyperVSocketGuestTransport` is implemented with raw libc calls because .NET's Linux `SocketPal` does not
map `AF_VSOCK`: `socket(AF_VSOCK=40, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC)`, `bind` to a
`sockaddr_vm { family = 40, port, cid = VMADDR_CID_ANY }`, `listen(1)`, and an `accept4` loop tolerating
`EINTR` and `EAGAIN` with a 100 ms sleep. A `fcntl(F_DUPFD_CLOEXEC)` call produces an independent write
descriptor.

Both `FileStream`s are created with `isAsync: false` on purpose: marking an accepted descriptor async makes
`FileStream` reject it, and independent read/write streams preserve full duplex because `FileStream`
serializes asynchronous operations per stream.

> The name `firecracker-vsock` is really "Linux `AF_VSOCK` with a configurable port" — the same transport
> serves macOS guests with a different port.

## Handshake

See the sequence diagram in [20-security/07-guest-isolation.md](../20-security/07-guest-isolation.md) for the
trust detail. Shape summary:

1. Guest → Host: `Hello` — workload and channel ids, image digest, artifact digest, an ephemeral P-256 SPKI,
   and a boot-token HMAC proof.
2. Host → Guest: `HostChallenge` — a 32-byte nonce and an expiry.
3. Guest → Host: `Proof` — ECDSA over the challenge payload.
4. Host → Guest: `GuestLease` — `accepted`, `reason_code`, `expires_at`, `maximum_frame_bytes`.

The guest requires `accepted` **and** `ExpiresAtUnixSeconds == boot lease expiry` verbatim.

After the lease, the guest starts the local broker proxy and enters the command loop, bounded by
`leaseCancellation.CancelAfter(leaseExpiresAt − now)`.

## Command set

### Inbound

| Message | Behaviour |
|---|---|
| `StartCommand` | Kind must equal the boot kind. `MaximumLogBytes` in `[1, 1 GiB]`; for kind 2, `MaximumOutputBytes` in `[1, 10 GiB]`. Starts the workload; a start failure emits `Exit(126, "workload-start-failed", …)` and ends the session. On success the guest emits `Health{running}` and begins observing the process (plus diagnostics for kinds 1 and 2). |
| `ProxyResponse` | Completes the pending local request by `RequestId`. An unknown id raises `InvalidDataException`. |
| `ShutdownCommand` | Stops the workload with `clamp(grace, 0, 60)`, emits `Exit(0, reason_code)`, and returns. |
| `Health` | Replies `running` or `ready`. |

### Outbound (non-proxy)

| Message | When |
|---|---|
| `Health` | After a successful start, and in reply to a health probe. |
| `Exit` | On termination: exit code, reason code, and a detail tail of at most 8 KiB. |
| `StreamChunk` | Every ~500 ms while `DiagnosticDetail` changes, on `stream_id = "runtime.logs"`. |

## Proxy purposes

The guest maps each local HTTP request to a purpose. Anything not listed raises
`UnauthorizedAccessException` inside the guest:

| Kind | Path | Purpose |
|---|---|---|
| `1` or `2` | `/mcp` | `mcp.runtime` |
| `0` or `2` | `/build/fetch` | `build.fetch` |
| `0` or `2` | `/build/artifact` | `build.artifact` |
| `0` or `2` | `/build/progress` | `build.progress` |
| anything else | — | `UnauthorizedAccessException` |

Note the asymmetry: kind 0 gets the `build.*` purposes but **not** `/mcp`, and kind 1 gets `/mcp` only.

## The local broker proxy

`GuestLocalBrokerProxy` is what the agent SDK connects to. It is deliberately minimal.

| Aspect | Rule |
|---|---|
| Transport | Unix socket only. On Windows it raises `PlatformNotSupportedException`. |
| Socket | Mode `0660`, group `csweet-workload`, backlog 16. |
| Method | `POST` only. |
| Paths | `/mcp`, `/build/fetch`, `/build/artifact`, `/build/progress`. |
| Protocol | HTTP/1.1. |
| Headers | At most 32 KiB. |
| Body | At most 16 MiB minus 64 KiB reserved for headers and the protobuf envelope, with **exactly one** framing mode: `Content-Length` xor `Transfer-Encoding: chunked`, with bounded trailers. |
| Forwarding | Strips `Host`, `Connection`, `Content-Length`, and `Transfer-Encoding` from requests; strips hop-by-hop headers and `Content-Length` from responses. |
| Responses | Always `Connection: close`. |
| Oversized input | HTTP 413 with a JSON-RPC error and the byte limit; no forwarding. Smaller configured frame ceilings are checked before sending the envelope. |
| Malformed input | `400 broker request rejected`. |

The SDK is pointed at it by the fixed environment: `CSweet__Agent__McpEndpoint = http://localhost/mcp` and
`CSweet__Agent__McpUnixSocketPath = <broker socket>`.

## Session end

When the command loop exits, `Program.cs` powers the VM off with `GuestSystemPower.PowerOffAsync`
(`/usr/bin/systemctl poweroff --no-block`, falling back to `/sbin/poweroff`, bounded at 5 seconds with
exceptions swallowed).

**The guest powers its own VM off.** The RuntimeHost lease and reaper are a backstop for the case where the
guest cannot do so.

## Sources

`src/CSweet.Office.RuntimeGuest/{Program.cs,GuestBrokerSession.cs,GuestBrokerTransport.cs,GuestServiceOptions.cs,GuestWorkloadSupervisor.cs,GuestLocalBrokerProxy.cs,GuestSystemPower.cs,GuestArtifactMaterializer.cs}`,
`src/CSweet.Office.WindowsSmokeTest/CertificationBrokerHost.cs`,
`tests/CSweet.Office.Tests/GuestLocalBrokerProxyTests.cs`.

Verified: 2026-09-16.
