# CSweet.Office.RuntimeHost

The privileged service. It is the only process that talks to a hypervisor, owns the authorization gate and its
ledgers, listens on the local RPC endpoint, hosts the platform helper subprocesses, runs the workload reaper,
and — on Windows — owns the `AF_HYPERV` guest-channel socket. It never listens on the network and never
creates a workload without a signed Headquarters authorization.

## Project facts

| Fact | Value |
|---|---|
| Output kind | worker service exe (`Microsoft.NET.Sdk.Worker`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `Runtime.Abstractions`, `Runtime.AppleVirtualization`, `Runtime.Firecracker`, `Runtime.HyperV`, `Runtime.LocalRpc`, `Runtime.Protocol` |
| Key package references | `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Hosting.WindowsServices`, `Microsoft.Extensions.Hosting.Systemd` |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.RuntimeHost`) |
| `InternalsVisibleTo` | not set |
| csproj `<Description>` | absent |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `RuntimeHostWorker` | `BackgroundService` | Logs the Windows identity it is running under (on Windows) and then runs `RuntimeHostRpcServer.RunAsync` for the process lifetime. |
| `RuntimeHostWorkloadReaper` | `BackgroundService` | A one-minute `PeriodicTimer` that calls `ReapAbandonedWorkloadsAsync` on every registered backend implementing `IPlatformWorkloadReaper`, logging the removal count and retrying on the next pass after a failure. `Interval` is an `internal static readonly TimeSpan` of one minute. |
| `HostPlatformProvider` | static class (internal) | `Resolve()` maps the running OS to the Hyper-V, Firecracker, or Apple Virtualization catalog descriptor. |

The rest of the service is composed from types owned by other projects: `RuntimeHostRpcServer`,
`RuntimeHostRequestDispatcher`, `RuntimeHostAuthorizationGate`, and `RuntimeHostEndpointOptions` from
`Runtime.LocalRpc`; `RuntimeHostAuthenticationOptions` and `RuntimeHostRequestAuthenticator` from
`Runtime.Protocol`; the three backend and connector types from the provider projects.

## Entry points / composition

`Program.cs` wires, in order:

1. `Host.CreateApplicationBuilder(args)`.
2. `AddWindowsService` with service name `CSweet.Office.RuntimeHost`, then `AddSystemd()`.
3. `RuntimeHostEndpointOptions` from the `CSweet:Office:RuntimeHost` section, followed immediately by
   `Validate()` — a bad pipe name, socket path, timeout, or frame limit fails startup.
4. `RuntimeHostAuthenticationOptions` from `CSweet:Office:RuntimeHost:Authentication`, then
   `LoadSharedKeyFileIfNeeded("%PROGRAMDATA%\CSweet\Office\runtime-host.key")`, the same path the Node uses.
5. Singletons: endpoint options, authentication options, `TimeProvider.System`, `RuntimeHostRequestAuthenticator`.
6. `RuntimeHostAuthorizationOptions` from `CSweet:Office:RuntimeHost:Authorization`, then
   `RuntimeHostAuthorizationGate`.
7. `RuntimeHostRequestDispatcher` and `RuntimeHostRpcServer`.
8. Hosted services: `RuntimeHostWorker`, then `RuntimeHostWorkloadReaper`.
9. Provider options, each from its own section: `CSweet:Office:Providers:HyperV`,
   `CSweet:Office:Providers:Firecracker`, `CSweet:Office:Providers:AppleVirtualization`.
10. `PlatformRuntimePayloadManifest.ApplyIfConfigured` for Firecracker and Apple Virtualization only. When a
    manifest path is configured, its verified values overwrite the options before any backend is built.
11. The operating-system switch that registers the backend:

| Host OS | Options singleton | Backend registered as `IPlatformIsolationBackend` |
|---|---|---|
| Windows | `HyperVIsolationBackendOptions` | `HyperVIsolationBackend` |
| Linux | `FirecrackerIsolationBackendOptions` | `FirecrackerIsolationBackend` |
| macOS | `AppleVirtualizationIsolationBackendOptions` | `AppleVirtualizationIsolationBackend` |

12. `HyperVSocketTransportOptions` from `CSweet:Office:RuntimeHost:HyperVSocket`, then `Validate()`, then the
    guest-channel connector registration:

| Host OS | Connector registered as `IPlatformGuestChannelConnector` |
|---|---|
| Windows | `WindowsHyperVSocketTransport`, also registered as the `IHyperVGuestTransport` singleton |
| Linux | `FirecrackerGuestChannelConnector` |
| macOS | `AppleVirtualizationGuestChannelConnector` |

13. `builder.Build().RunAsync()`.

## Configuration surface in `appsettings.json`

The shipped file carries logging configuration and empty provider placeholders. The installer rewrites the
secrets and paths.

| Section | Keys |
|---|---|
| `Logging:LogLevel` | `Default`, `Microsoft.Hosting.Lifetime` |
| `Logging:EventLog:LogLevel` | `Default` |
| `CSweet:Office:RuntimeHost` | `NamedPipeName`, `UnixSocketPath`, `ConnectTimeoutSeconds`, `MaximumFrameBytes` |
| `CSweet:Office:RuntimeHost:Authentication` | `KeyId`, `SharedKeyBase64`, `SharedKeyFilePath` |
| `CSweet:Office:Providers:HyperV` | `HelperExecutablePath`, `HelperExecutableDigest`, `GuestImagePath`, `GuestImageDigest`, `GuestImageSignaturePath`, `GuestImageSigningCertificatePath`, `GuestImageSigningCertificateThumbprint`, `ArtifactImageRoot`, `BrokerProtocolVersion`, `CertificationSuiteVersion`, `CertificationEvidencePath`, `CertificationEvidenceDigest`, `CertifiedAt`, `CertificationExpiresAt` |
| `CSweet:Office:Providers:Firecracker` | The same keys plus `PayloadManifestPath`, `GuestChannelConnectTimeoutSeconds`, `RequiredGuestChannelTransport` |
| `CSweet:Office:Providers:AppleVirtualization` | The same keys plus `PayloadManifestPath`, `GuestChannelConnectTimeoutSeconds`, `RequiredGuestChannelTransport` |

Two sections that the code reads are **not** in the shipped file: `CSweet:Office:RuntimeHost:Authorization`
(written by the Windows installer, which supplies the state directory and the 600 s / 120 s windows) and
`CSweet:Office:RuntimeHost:HyperVSocket` (Windows only). The Hyper-V provider section deliberately has no
`PayloadManifestPath`, because `PlatformRuntimePayloadManifest.ApplyIfConfigured` is only called for
Firecracker and Apple Virtualization.

## Behaviour worth knowing

- **The service is a listener for privileged requests only.** It has no inbound network listener; the only
  listener is the pipe or Unix socket, created with an exact ACL, and every frame must pass
  `RuntimeHostRequestAuthenticator` before dispatch
  ([SEC-INV-12](../../20-security/11-security-invariants.md),
  [SEC-INV-13](../../20-security/11-security-invariants.md)).
- **Authorization is unconditional.** The dispatcher's create path always calls
  `RuntimeHostAuthorizationGate.ValidateAndCommit` first; the Node-side client has no unsigned create path
  ([SEC-INV-07](../../20-security/11-security-invariants.md)).
- **The reaper is asymmetric by design.** It only touches backends that implement `IPlatformWorkloadReaper`
  and calls one method with no arguments, so cleanup can never depend on control-plane state. The Apple
  helper's own reap loop only considers runtime workloads (`kind == 1`); the Hyper-V helper reaps runtime and
  toolchain-build instances but skips builder instances; see
  [30-workloads/07-provider-backends.md](../../30-workloads/07-provider-backends.md).
- **Helper identity is re-verified per invocation.** Backends and connectors hash the configured helper
  executable before every start and re-check the guest image and artifact media digests, so a payload that was
  swapped after certification cannot be used ([SEC-INV-20](../../20-security/11-security-invariants.md)).
- **Windows is the only host with a native guest channel.** `WindowsHyperVSocketTransport` constructs a raw
  `AF_HYPERV` socket by hand (address family 34, protocol 1, a 36-byte `SOCKADDR_HV`), polls short non-blocking
  connect attempts because the managed `ConnectAsync` path rejects the non-IP family, and hands the accepted
  socket back as a `NetworkStream`. The helper does not open this socket.
- **Failures are typed, sanitized, and correlated.** Dispatcher errors carry an error code and a message that
  never includes raw host paths; the request id is echoed so Node logs and RuntimeHost logs can be joined.
- **The gate's validation order and fencing rule are load-bearing.** See
  [20-security/04-workload-authorization.md](../../20-security/04-workload-authorization.md) and
  [SEC-INV-08](../../20-security/11-security-invariants.md)/[SEC-INV-09](../../20-security/11-security-invariants.md).
  The replay ledger is never pruned.

> **Out of repo:** Headquarters signs the workload authorization and owns the production guest broker
> implementation. The only in-repo broker implementation is the test-only
> `src/CSweet.Office.WindowsSmokeTest/CertificationBrokerHost.cs`.

## Related tests

There is no test that starts `RuntimeHost.exe`. The service's pieces are covered through
`RuntimeHostRpcIntegrationTests`, which builds a real `RuntimeHostRpcServer` over a real transport with the
same options and authorization gate types, plus `RuntimeHostAuthenticationTests`,
`RuntimeHostProtocolMapperTests`, `PlatformIsolationBackendTests`, `HyperVInstanceReapingTests` (the
`ShouldReap` predicate both helpers use), and `FirecrackerHelperSecurityTests`. The Windows service
registration itself is asserted textually by `WindowsHyperVOnboardingTests` against the installer scripts.

## Related documentation

- [10-system/03-components.md](../../10-system/03-components.md) — where this service sits and what it owns.
- [20-security/06-host-privilege-model.md](../../20-security/06-host-privilege-model.md) — the account and
  ACL model per platform.
- [20-security/08-provider-certification.md](../../20-security/08-provider-certification.md) — what a provider
  must present before this service will use it.
- [30-workloads/07-provider-backends.md](../../30-workloads/07-provider-backends.md) — create, start, log, and
  reap mechanics per backend.
- [10-system/06-process-network-and-storage-surface.md](../../10-system/06-process-network-and-storage-surface.md)
  — listeners, sockets, and state roots.
- [../configuration.md](../configuration.md) and [../file-layout.md](../file-layout.md).

## Sources

`src/CSweet.Office.RuntimeHost/CSweet.Office.RuntimeHost.csproj`,
`src/CSweet.Office.RuntimeHost/{Program.cs,RuntimeHostWorker.cs,RuntimeHostWorkloadReaper.cs,HostPlatformProvider.cs,appsettings.json}`,
`src/CSweet.Office.Runtime.LocalRpc/{RuntimeHostRpcServer,RuntimeHostRequestDispatcher,RuntimeHostAuthorizationGate}.cs`,
`tests/CSweet.Office.Tests/{RuntimeHostRpcIntegrationTests,HyperVInstanceReapingTests}.cs`.

Verified: 2026-09-15.
