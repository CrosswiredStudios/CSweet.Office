# CSweet.Office.RuntimeHost

The privileged service. It is the only process that talks to a hypervisor, owns the authorization gate and its ledgers, listens on the local RPC endpoint, hosts the platform helper subprocesses, runs the workload reaper, and — on Windows — owns the `AF_HYPERV` guest-channel socket. It never listens on the network and never creates a workload without a signed Headquarters authorization.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/runtime-host.md`](../../docs/80-reference/projects/runtime-host.md).

**Output:** worker service exe
**References:** `CSweet.Office.Runtime.Abstractions`, `CSweet.Office.Runtime.AppleVirtualization`, `CSweet.Office.Runtime.Firecracker`, `CSweet.Office.Runtime.HyperV`, `CSweet.Office.Runtime.LocalRpc`, `CSweet.Office.Runtime.Protocol`

## What it owns

- `RuntimeHostWorker` — runs `RuntimeHostRpcServer.RunAsync` for the process lifetime.
- `RuntimeHostWorkloadReaper` — a one-minute `PeriodicTimer` that calls `ReapAbandonedWorkloadsAsync` on every registered backend implementing `IPlatformWorkloadReaper`.
- `Program.cs` — composition: endpoint, authentication, and authorization options; manifest application for Firecracker and Apple; the per-OS backend and connector registrations.
- `HostPlatformProvider` — maps the running OS to the Hyper-V, Firecracker, or Apple catalog descriptor.
- The services it hosts from elsewhere: `RuntimeHostRpcServer`, `RuntimeHostRequestDispatcher`, and `RuntimeHostAuthorizationGate` from `Runtime.LocalRpc`, and `RuntimeHostRequestAuthenticator` from `Runtime.Protocol`.

## Notes for contributors

- There is no unsigned create path (SEC-INV-07), and the authorization gate commits the replay ledger last (SEC-INV-08); never reorder that validation sequence.
- The shipped `appsettings.json` carries logging plus empty provider placeholders; the installer rewrites secrets and paths. The `Authorization` and `HyperVSocket` sections are read by code but written by the installer, not the file.
- The reaper only sees `IPlatformWorkloadReaper` and passes no arguments, so cleanup can never depend on control-plane state.
- Helper identity is re-verified per invocation, and the Windows backend opens its own guest channel; both are deliberate (SEC-INV-20).
- No test starts `RuntimeHost.exe`; the pieces are covered through a real server and transport built by `RuntimeHostRpcIntegrationTests`, and the Windows service registration is asserted textually against the installer scripts.

## Tests

`RuntimeHostRpcIntegrationTests`, `RuntimeHostAuthenticationTests`, `RuntimeHostProtocolMapperTests`, `PlatformIsolationBackendTests`, `HyperVInstanceReapingTests`, and `FirecrackerHelperSecurityTests`.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
