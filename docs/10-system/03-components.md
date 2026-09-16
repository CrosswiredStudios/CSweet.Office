# Component map

**Audience:** everyone; prerequisite for the security and workload pages.

Every executable in the system, what identity it runs under, what it starts, and what it is allowed to do.
For ports, sockets, and directories see
[06-process-network-and-storage-surface.md](06-process-network-and-storage-surface.md).

## Host services

### `CSweet.Office.Node`

Worker service (`Microsoft.NET.Sdk.Worker`), Windows service name `CSweet.Office.Node`, Linux unit
`csweet-office-node.service`, macOS launchd label from `com.csweet.office.node.plist`.

- Runs as `NT SERVICE\CSweet.Office.Node` on Windows; `csweet-node` on Linux; a dedicated account on macOS.
- **Owns:** the Office identity (`node-state.json`, `node-identity.pfx`), enrollment, certificate renewal and
  recovery, the heartbeat, assignment validation, artifact cache, and the guest-channel relay.
- **Does not:** touch Hyper-V, write RuntimeHost state, or hold any Headquarters signing key.
- **Wired in `Program.cs`:** `OfficeOptions`, `ControlPlaneServerCertificateValidator`, `OfficeStateStore`,
  `RuntimeHostInventory`, `OfficeArtifactCache`, `OfficeWorker`, `TimeProvider.System`, a named
  `HttpClient` called `control-plane`, plus the local-RPC client half (`RuntimeHostEndpointOptions`,
  `RuntimeHostAuthenticationOptions`, `RuntimeHostRequestAuthenticator`) and a single
  `IAgentIsolationProvider` built from `HostPlatformProvider.Resolve()` — which is the
  `RuntimeHostProviderClient`.
- Startup refuses a `ControlPlaneUrl` that is not an absolute HTTPS URL.

### `CSweet.Office.RuntimeHost`

Worker service, Windows service name `CSweet.Office.RuntimeHost`, Linux unit
`csweet-office-runtime.service`, macOS launchd label from `com.csweet.office.runtime.plist`.

- Runs as `NT SERVICE\CSweet.Office.RuntimeHost` on Windows (and is the only account added to
  `Hyper-V Administrators`); `root` on Linux and macOS.
- **Owns:** the authorization gate and its three ledgers, the local RPC server, the provider backends, the
  platform helpers, the workload reaper, and — on Windows — the Hyper-V guest-channel socket.
- **Does not:** listen on the network, or accept a workload without a signed authorization.
- `RuntimeHostWorker` logs the Windows identity it is running under and then runs the RPC server forever.
  `RuntimeHostWorkloadReaper` runs a one-minute timer and calls `ReapAbandonedWorkloadsAsync` on each
  backend.

### `CSweet.Office.Maintenance`

A third Windows service created by `Install-CSweetOfficeRuntimeHost.ps1`, described as *"Receives authorized
C-Sweet Office repair requests. No inbound listener."* It runs
`CSweet.Office.Configurator.exe --maintenance-service "--settings=<maintenance-settings.json>"` as
`SYSTEM`. See [40-operations/05-repair-and-recovery.md](../40-operations/05-repair-and-recovery.md).

## The `RuntimeHostProviderClient` seam

This is the single most important structural fact in the codebase: **the Node talks to the RuntimeHost
through the same `IAgentIsolationProvider` abstraction that a provider backend implements.**

| Method | Behaviour |
|---|---|
| `ProbeAsync` | Sends `ProbeRequest` and merges the returned descriptor (version, OS, arch, assurance, capabilities) over the local descriptor from `HostPlatformProvider`. |
| `CreateAuthorizedAsync` | Sends `CreateWorkloadRequest` plus the `SignedWorkloadAuthorization`. |
| `CreateAsync` | **Throws** `IsolationUnavailableException` — *"RuntimeHost creation requires a signed Headquarters workload authorization."* There is no unsigned path. |
| `StartAsync` / `InspectAsync` / `StopAsync` / `DestroyAsync` / `ReadLogsAsync` | Send the corresponding envelopes, each carrying a handle the RuntimeHost must find in its handle ledger. |
| `OpenGuestChannelAsync` | Sends `OpenGuestChannelRequest`, reads exactly one signed response frame, then hands the same stream back as the guest channel. |
| `PinHeadquartersTrustAsync` | Sends `PinHeadquartersTrustRequest`; write-once in the RuntimeHost. |

`OfficeWorker.ExecuteAssignmentAsync` requires the resolved provider to implement both `IRuntimeHostClient`
and `IAgentGuestChannelProvider`, and fails with `IsolationUnavailableException` otherwise.

## Platform helpers and backends

| Platform | Backend (in RuntimeHost) | Helper executable | Guest channel |
|---|---|---|---|
| Windows | `HyperVIsolationBackend` | `CSweet.Office.Runtime.HyperV.Helper` (C# EXE) | Native `AF_HYPERV` socket opened by `WindowsHyperVSocketTransport` in the RuntimeHost — **not** by the helper |
| Linux | `FirecrackerIsolationBackend` | `CSweet.Office.Runtime.Firecracker.Helper` (C# EXE) | Helper stdio bridge to the jail's `guest.vsock` using `CONNECT 5000` |
| macOS | `AppleVirtualizationIsolationBackend` | `CSweet.Office.Runtime.AppleVirtualization.Helper` (**Swift** executable) | Helper requests a manager socket that connects `VZVirtioSocketDevice` and passes the descriptor back |

All three C# backends derive from `ExternalPlatformIsolationBackend` in `Runtime.Core`; the Swift helper
implements the same JSON-over-stdio contract by hand. See
[30-workloads/07-provider-backends.md](../30-workloads/07-provider-backends.md) and
[20-security/09-helper-protocol.md](../20-security/09-helper-protocol.md).

## In-guest components

| Executable | Installed at | Workload kind | Purpose |
|---|---|---|---|
| `CSweet.Office.RuntimeGuest` | `/usr/lib/csweet/guest/` | n/a | Boot-configured broker: materializes artifacts, performs the handshake, proxies agent requests, supervises the workload process. |
| `CSweet.Office.BuilderGuest` | `/usr/lib/csweet/builder/` | `0` | Builds a plugin from a repository, fetching the source and dependencies through the broker. |
| `CSweet.Office.ToolchainGuest` | `/usr/lib/csweet/toolchain/` | `2` | Runs a certified adapter from a materialized package and uploads a deterministic output bundle. |
| `CSweet.Office.GuestProbe` | not installed | — | Certification-only executable. Used by the Hyper-V isolation smoke test to prove the guest channel and broker work. |

`CSweet.Office.RuntimeGuest` is the only one of these present in every guest image.

## Certification and test executables

- `CSweet.Office.WindowsSmokeTest` — runs a real Hyper-V or Firecracker guest, and **only after isolation
  checks pass** emits development certification evidence. It contains the only in-repo host-side guest
  broker implementation, `CertificationBrokerHost.cs`, and requires administrator (Windows) or root (Linux).
- `CSweet.Office.GuestProbe` — the in-guest half of that check.
- `tests/CSweet.Office.Tests` — xUnit; the de-facto specification for the security-critical paths.

## Configurator

`CSweet.Office.Configurator` (Windows-only `.exe`) parses the `csweet-office://enroll/...` handoff URI
carrying `handoff`, `session`, `origin`, and `certificate` query parameters. It re-launches itself elevated
(`runas`). With `--maintenance-service` it becomes the maintenance host described above.

## Sources

`CSweet.Office.slnx`, `CSweet.Office.Independent.slnx`, `src/CSweet.Office.Node/{Program.cs,OfficeWorker.cs,ProviderInventory.cs,HostPlatformProvider.cs}`,
`src/CSweet.Office.RuntimeHost/{Program.cs,RuntimeHostWorker.cs,RuntimeHostWorkloadReaper.cs,appsettings.json}`,
`src/CSweet.Office.Runtime.LocalRpc/{RuntimeHostProviderClient.cs,RuntimeHostRpcServer.cs,RuntimeHostRequestDispatcher.cs}`,
`src/CSweet.Office.Runtime.Core/ExternalPlatformIsolationBackend.cs`,
`src/CSweet.Office.Runtime.HyperV/HyperVSocketTransport.cs`,
`src/CSweet.Office.RuntimeGuest/Program.cs`, `src/CSweet.Office.Configurator/Program.cs`,
`src/CSweet.Office.WindowsSmokeTest/CertificationBrokerHost.cs`,
`scripts/windows/Install-CSweetOfficeRuntimeHost.ps1`, `build/linux-firecracker/provision-guest.sh`.

Verified: 2026-09-15.
