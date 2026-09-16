# CSweet.Office.RuntimeGuest

The in-guest broker. It is the only Office executable present in every guest image, and it is the only thing
inside the VM that talks to the host. It materializes the artifact DVD into a disposable path, performs the
authenticated handshake with the host-side broker session, exposes a guest-local HTTP endpoint on a Unix
socket for the agent SDK, forwards those requests through the authenticated channel, launches the workload as
an unprivileged user, and shuts the guest down when the session ends. It references `Runtime.Protocol` only, so
no runtime or provider library reaches the image.

## Project facts

| Fact | Value |
|---|---|
| Output kind | console exe (`Microsoft.NET.Sdk`, `<OutputType>Exe</OutputType>`, `AllowUnsafeBlocks=true`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `Runtime.Protocol` (the sole exception to the no-references rule for guests) |
| Key package references | none of its own |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.RuntimeGuest`) |
| `InternalsVisibleTo` | `CSweet.Office.Tests` |
| csproj `<Description>` | "Minimal in-guest C-Sweet broker service and workload supervisor." |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `GuestServiceOptions` | record | Everything the broker was booted with, plus `FromEnvironment()`, `FromBootConfiguration(boot)`, and `Validate(TimeProvider)` with the boot-integrity rules. |
| `IGuestBrokerTransport` / `StandardIoGuestBrokerTransport` | interface / class | The transport the broker accepts; stdio for the smoke path. |
| `LinuxHyperVSocketGuestTransport` | class | The real transport: an `AF_VSOCK` listener implemented with P/Invoke, exposing the accepted descriptor as two independent synchronous `FileStream`s. |
| `GuestBrokerConnection` | record | Input and output streams, with an `IAsyncDisposable` that handles the single-stream case. |
| `GuestBootConfigurationReader` | static class | Reads exactly one `BootConfiguration` envelope from the host. |
| `GuestBrokerSession` | class | The session state machine: hello/challenge/proof/lease, start command, proxy forwarding, health, exit, and shutdown. |
| `GuestArtifactMaterializer` | class | Mounts the artifact DVD read-only, verifies the bundle digest, and expands it beneath a fixed disposable root. |
| `GuestLocalBrokerProxy` | class | A hand-written HTTP/1.1 server on a guest-local Unix socket that accepts only `POST /mcp`, `/build/fetch`, `/build/artifact`, and `/build/progress`. |
| `GuestLocalBrokerRequest` / `GuestLocalBrokerResponse` | records | Method, path, headers, bounded body. |
| `IGuestWorkloadSupervisor` / `GuestWorkloadSupervisor` | interface / class | Starts the workload through `setpriv`, drains bounded logs, reports a diagnostic tail, and stops the process tree. |
| `GuestUnixFilePermissions` | static class (internal) | Applies the `csweet-workload` group to the broker socket, workload token, and artifact tree. |
| `GuestSystemPower` | static class (internal) | Powers the guest off when the host-bound session ends. |

## Entry points / composition

`Program.cs` selects the transport from `CSWEET_GUEST_BROKER_TRANSPORT` — `stdio` (default),
`hyperv-vsock`, or `firecracker-vsock` (the last one reading `CSWEET_GUEST_VSOCK_PORT`, default 5000, and
requiring the range 1024-65535; anything else throws). It then:

1. Accepts one connection from the transport.
2. Loads options either from the boot configuration envelope (when the transport is a host boot channel) or
   from environment variables (the stdio path).
3. Validates them, and for workload kinds 1 and 2 materializes the artifact bundle into the disposable root.
4. On a boot-configuration failure and a host boot channel, writes one `GuestBootFailure` envelope with a
   reason code (`guest-boot-invalid`, `guest-boot-io-failed`, `guest-boot-failed`) and which re-throws.
5. Constructs `GuestWorkloadSupervisor` and `GuestBrokerSession` and runs the session.
6. In the `finally`, powers the guest off when the transport is a host boot channel.

## Behaviour worth knowing

- **`GuestBrokerSession` is the counterpart of the test-only host broker.** It writes `Hello`, requires a
  `Challenge`, answers with `Proof`, requires an accepted `Lease` matching the boot-bound expiry, then serves
  the host's commands. A lease deadline cancels the whole session
  ([SEC-INV-23](../../20-security/11-security-invariants.md)).
- **The local broker endpoint is an allow-list by workload kind.** `PurposeFor(kind, path)` maps `(1, "/mcp")`
  and `(2, "/mcp")` to `mcp.runtime`; `(0|2, "/build/fetch"|"/build/artifact"|"/build/progress")` to
  `build.fetch`, `build.artifact`, `build.progress`. Anything else throws `UnauthorizedAccessException`, and a
  builder workload cannot reach `mcp.runtime`.
- **The proxy is a hand-rolled HTTP/1.1 parser with tight limits.** 32 KiB of headers, 1 MiB of body,
  `Content-Length` or `Transfer-Encoding: chunked` but never both, duplicate headers rejected, hop-by-hop
  headers stripped, and always `Connection: close`. `GET` and unknown paths are rejected before any forwarding.
- **The supervisor clears the environment and rebuilds it from an allow-list.** `start.Environment.Clear()`
  then sets `PATH`, `HOME=/tmp/csweet-agent`, `CSWEET_BROKER_ONLY=1`,
  `CSweet__Agent__McpEndpoint=http://localhost/mcp`, `CSweet__Agent__McpUnixSocketPath`,
  `CSweet__Agent__ManifestPath=csweet-plugin.json`, and for runtime and toolchain kinds
  `CSweet__Agent__InstallationId`, `CSweet__Agent__BusinessId`, `CSweet__Agent__RuntimeInstanceId`,
  `CSweet__Agent__TickId`, and `CSweet__Agent__WorkloadTokenFile`. Host
  commands may add at most 32 entries, each from the fixed 17-key list in `IsAllowedEnvironmentKey`, each at
  most 4096 characters and NUL-free ([SEC-INV-21](../../20-security/11-security-invariants.md)).
- **The workload runs unprivileged.** On Linux the process is started through
  `/usr/bin/setpriv --reuid=csweet-workload --regid=csweet-workload --init-groups --no-new-privs -- <exe>`,
  with the working directory fixed to the artifact root. The executable must resolve inside the read-only
  artifact root; the single exception is the certified toolchain runner at
  `/usr/lib/csweet/toolchain/CSweet.Office.ToolchainGuest` for kind 2.
- **Log limits kill the workload.** `DrainBoundedAsync` counts bytes from both streams and, once past
  `maximumLogBytes`, kills the process tree and throws, which the session reports as exit 137 with
  `resource-limit-exceeded`. The last 8 KiB of output is kept as a diagnostic tail and streamed as
  `runtime.logs` chunks.
- **Artifact materialization is a mount, not a path.** `GuestArtifactMaterializer` mounts `/dev/sr0` (or the
  explicitly approved `/dev/vdc`) at `/run/csweet/artifact-media` with `RDONLY|NOSUID|NODEV|NOEXEC`, requires
  exactly one `artifact.csab*` file, hashes the whole bundle against the boot-bound digest, then extracts with
  the entry-count, path, duplicate, link, and 2 GiB expanded-size limits, and always unmounts. Destination
  roots must stay beneath `/run/csweet`; modes are normalized so nothing is world-writable and only the
  entrypoint keeps execute bits ([SEC-INV-22](../../20-security/11-security-invariants.md)).
- **The VSOCK transport is hand-rolled because .NET cannot do it.** The comment in
  `LinuxHyperVSocketGuestTransport` records that the Linux `SocketPal` does not map `AF_VSOCK`, so the
  listener is created with `socket`/`bind`/`listen`/`accept4` P/Invokes and the accepted descriptor is split
  into a read `FileStream` and a `fcntl`-duplicated write `FileStream` — with `isAsync: false`, because
  `FileStream` rejects the accepted handle when it is marked asynchronous.
- **Guest file permissions are group-based.** The broker socket, workload token file, and artifact tree are
  chgrp'd to `csweet-workload` so the unprivileged workload can read them without the broker relaxing modes.
- **`GuestServiceOptions.Validate` is the boot-integrity gate.** It requires both GUIDs, protocol version
  `1.0`, lowercase SHA-256 image and artifact digests, a boot token of at least 16 characters, an unexpired
  lease, an absolute artifact root, the exact fixed artifact root `/run/csweet/artifact/payload` for kinds 1
  and 2, a complete runtime identity for those kinds, guest-runtime paths beneath `/run/csweet`, and a frame
  limit between 4096 and 16 MiB.

> **Out of repo:** the guest envelope types, `GuestBootConfiguration`, `GuestHandshakeClient`,
> `ExpectedGuestIdentity`, `StartCommand`, `GuestHealth`, `GuestExit`, `ProxyRequest`/`ProxyResponse`, and
> `LengthDelimitedProtobuf` come from the `CSweet.Office.Contracts` package. Headquarters owns what the agent
> can do with `mcp.runtime`; this project only relays the frames.

## Related tests

| Test class | What it pins |
|---|---|
| `GuestArtifactMaterializerTests` | That only the provider-owned fixed artifact devices are accepted and arbitrary guest paths are rejected. |
| `GuestLocalBrokerProxyTests` | That a bounded chunked JSON body is accepted, and that conflicting body framing is rejected. |
| `WindowsHyperVOnboardingTests` | Includes the native `sockaddr_vm` layout check and the assertion that the guest keeps the scratch mount inside the broker process, plus the vsock descriptor behaviour. |
| `OfficeTests` | The guest-envelope and mapper paths that the session uses. |

## Related documentation

- [30-workloads/04-guest-broker-protocol.md](../../30-workloads/04-guest-broker-protocol.md) — handshake,
  purposes, and proxy framing.
- [20-security/07-guest-isolation.md](../../20-security/07-guest-isolation.md) — mount flags, the environment
  allow-list, and the `setpriv` launch.
- [30-workloads/02-runtime-workload-lifecycle.md](../../30-workloads/02-runtime-workload-lifecycle.md) —
  where this executable sits in a running workload.
- [30-workloads/05-artifacts-and-media.md](../../30-workloads/05-artifacts-and-media.md) and
  [30-workloads/06-guest-images.md](../../30-workloads/06-guest-images.md).
- [../wire-protocols.md](../wire-protocols.md) and [../status-and-error-codes.md](../status-and-error-codes.md).

## Sources

`src/CSweet.Office.RuntimeGuest/CSweet.Office.RuntimeGuest.csproj`,
`src/CSweet.Office.RuntimeGuest/{Program,GuestServiceOptions,GuestBrokerSession,GuestBrokerTransport,GuestLocalBrokerProxy,GuestArtifactMaterializer,GuestWorkloadSupervisor,GuestUnixFilePermissions,GuestSystemPower}.cs`,
`tests/CSweet.Office.Tests/{GuestArtifactMaterializerTests,GuestLocalBrokerProxyTests,WindowsHyperVOnboardingTests}.cs`.

Verified: 2026-09-15.
