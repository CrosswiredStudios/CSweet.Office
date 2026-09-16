# CSweet.Office.Runtime.HyperV

The Windows provider library: the Hyper-V backend that plugs into RuntimeHost, the hand-rolled `AF_HYPERV`
socket transport that opens the guest broker channel, the host readiness probe, the optional-feature
provisioner, the elevated RuntimeHost install/repair launcher, and the progress store those launchers write.
It derives from `ExternalPlatformIsolationBackend`, so the actual VM lifecycle happens in the privileged
helper; this library owns everything around it.

## Project facts

| Fact | Value |
|---|---|
| Output kind | library (`Microsoft.NET.Sdk`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `Runtime.Core` |
| Key package references | none of its own (`Microsoft.Win32.Registry` usage comes from the framework) |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.Runtime.HyperV`) |
| `InternalsVisibleTo` | `CSweet.Office.Tests` |
| csproj `<Description>` | "Certified Hyper-V Generation 2 isolation backend." |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `HyperVIsolationBackendOptions` | class | Marker subclass of `PlatformIsolationBackendOptions`; Hyper-V keeps the native socket transport rather than the stdio one. |
| `HyperVIsolationBackend` | class | `ExternalPlatformIsolationBackend` + `IPlatformWorkloadReaper`; `IsHostPlatform` requires Windows. |
| `WindowsHyperVSocketTransport` | class | Implements `IHyperVGuestTransport` and `IPlatformGuestChannelConnector`. Opens the guest broker channel over a raw Hyper-V socket. |
| `HyperVSocketTransportOptions` | class | `LinuxVsockPort` (default 2761), `ConnectTimeoutSeconds` (60), `RetryDelayMilliseconds` (250), `ServiceId`, `Validate()`, and the static `LinuxVsockServiceId(port)` GUID template `{port:x8}-facb-11e6-bd58-64006a7986d3`. |
| `WindowsHyperVSocketServiceRegistration` | static class | Registry helper: `RegistryBasePath`, `ServiceKeyPath(serviceId)`, `LegacyBracedServiceKeyPath(serviceId)`. |
| `IHyperVGuestTransport` | interface | `ConnectAsync(virtualMachineId)` — the test seam under the connector. |
| `WindowsHyperVHostProbe` | class, `IWindowsHyperVHostProbe` | Reads product name and edition from the registry, queries `Microsoft-Hyper-V` state through DISM, checks the four processor features and physical memory, and caches a result for 30 seconds. |
| `WindowsHyperVHostReadiness` | record | The readiness facts plus `HardwareRequirementsSatisfied` and restart-pending state. |
| `WindowsOptionalFeatureState` | enum | `Unknown`, `Disabled`, `Enabled`, `EnablePending`, `DisablePending`. |
| `IWindowsHyperVFeatureProvisioner` / `WindowsHyperVFeatureProvisioner` | interface / class | Launches an elevated `dism /Enable-Feature /FeatureName:Microsoft-Hyper-V /NoRestart` after checking edition, hardware, and interactivity. |
| `IWindowsRuntimeHostProvisioner` / `WindowsRuntimeHostProvisioner` | interface / class | Resolves and launches the packaged installer, the development bootstrap, or the access-repair script elevated, writes progress, and interprets progress state (including interrupted and timed-out runs). |
| `WindowsRuntimeHostProvisioningInfo` | record | Mode (`Unavailable`, `PackagedInstaller`, `DeveloperBootstrap`, `AccessRepair`), launchability, label, description. |
| `WindowsRuntimeHostProvisioningProgress` | record | Job id, workflow, state, phase key and display name, message, percent, timestamps, remaining-time estimate, restart flag, error fields, owner process id. |
| `WindowsRuntimeHostInstallResult` / `WindowsHyperVEnablementResult` | records | Success flag, error code, operator-facing message, and whether an elevation prompt was started. |
| `WindowsRuntimeHostProgressStore` | static class (internal) | Reads and validates the `windows-isolation-*.json` progress documents under `%PROGRAMDATA%\CSweet\Setup`, including the rule that a fresh process only adopts a still-running job. |

## Entry points / composition

No executable. `CSweet.Office.RuntimeHost.Program.cs` registers `HyperVIsolationBackendOptions` and
`HyperVIsolationBackend` when `OperatingSystem.IsWindows()`, plus `WindowsHyperVSocketTransport` as both
`IHyperVGuestTransport` and `IPlatformGuestChannelConnector`. The Windows installer scripts provision the
helper directories and the registry service key; this library only reads them.

## Behaviour worth knowing

- **The socket is hand-rolled.** `WindowsHyperVSocketTransport` uses address family 34 and protocol 1, builds
  a 36-byte `SOCKADDR_HV` (`HyperVSocketEndPoint.Serialize` writes the VM GUID at offset 4 and the service GUID
  at offset 20), and connects through short non-blocking attempts with `Socket.Poll`, because the managed
  `ConnectAsync`/`ConnectEx` path rejects the non-IP family with `WSAEINVAL` (10022). The code comment records
  both that fact and the reason for polling rather than blocking: a blocking connect would consume the whole
  readiness deadline while Linux is still booting.
- **The guest channel is opened by RuntimeHost, not by the helper.** The helper creates and configures the
  VM, but `WindowsHyperVSocketTransport.OpenGuestChannelAsync` parses `ProviderInstanceId` as a GUID in `N`
  format and connects to `ServiceId`. This is the structural difference from the Linux and macOS backends,
  which tunnel the channel through the helper's stdio.
- **The service id is derived from the port.** `HyperVSocketTransportOptions.ServiceId` is
  `LinuxVsockServiceId(LinuxVsockPort)`, so a port change changes the registry key the guest must be listening
  under. The helper verifies the key exists for the `CSWEET_HYPERV_BROKER_SERVICE_ID` environment value.
- **The host probe is bounded and cached.** DISM is invoked with a 15-second timeout, output is read with a
  128 KiB ceiling on stdout and 16 KiB on stderr, and feature state is parsed from a small set of known
  strings — anything else is `Unknown`. The result is cached for 30 seconds behind a semaphore.
- **Readiness is about the hypervisor, not just the feature.** `HardwareRequirementsSatisfied` accepts either
  a present hypervisor or the combination of SLAT, firmware virtualization, DEP, and at least 4 GiB of RAM.
  `IsHyperVRestartPending` treats `EnablePending`, or `Enabled` without a hypervisor while Windows has a
  pending restart, as needing a reboot.
- **Provisioning never runs a command line supplied by Headquarters.** `LaunchElevated` builds a fixed
  `powershell.exe` invocation for a resolved `.ps1` file, passes the current user SID, progress path and job
  id, and refuses an installer whose directory lacks `CSweet.WindowsSetupProgress.ps1` and
  `payload\runtime-manifest.json` ([SEC-INV-19](../../20-security/11-security-invariants.md) covers the
  maintenance-service half of the same boundary).
- **The staged enrollment token is a file, not an argument.** When a control-plane URL and token are supplied,
  the token is written to a `LocalApplicationData\CSweet\Setup` file with write-through and only the path is
  passed ([SEC-INV-21](../../20-security/11-security-invariants.md) governs the guest-side analogue).
- **Progress documents are treated as untrusted input.** `WindowsRuntimeHostProgressStore` rejects reparse
  points, sizes above 64 KiB, wrong schema versions, out-of-range percentages, inconsistent timestamps, and
  over-long strings; a running job is only adopted across an application restart when Windows has not
  rebooted since it started.

> **Out of repo:** the installer payload layout, the `runtime-manifest.json` contract, and the signed release
> artifacts the provisioner resolves are produced by the release pipeline and Headquarters, not by this
> library.

## Related tests

| Test class | What it pins |
|---|---|
| `WindowsHyperVOnboardingTests` | The largest Windows-side suite: PowerShell diagnostic decoding (CLIXML and plain text), supported-edition rules, feature-state parsing, restart-pending logic, helper argument rules, the VSOCK service-id template, the unbraced registry key name, and a long series of installer-script assertions (service registration, ACLs, digest skipping, reconnect semantics, recovery staging). |
| `HyperVInstanceReapingTests` | `HyperVHelperController.ShouldReap`: a running VM with an active lease is retained, an expired lease is reaped, a powered-off runtime is reaped before lease expiry, and a newly created instance is protected by the creation grace period. |
| `PlatformIsolationBackendTests` | That the Hyper-V backend probe fails closed without an installed helper or certification. |
| `RuntimeHostRpcIntegrationTests` | The probe-gating behaviour that makes a Hyper-V backend without a guest-channel connector unavailable. |

## Related documentation

- [30-workloads/07-provider-backends.md](../../30-workloads/07-provider-backends.md) — Hyper-V create, start,
  log, and reap mechanics.
- [20-security/09-helper-protocol.md](../../20-security/09-helper-protocol.md) — the protocol the backend
  speaks to the helper.
- [10-system/06-process-network-and-storage-surface.md](../../10-system/06-process-network-and-storage-surface.md)
  — the registry key and the data root.
- [20-security/06-host-privilege-model.md](../../20-security/06-host-privilege-model.md) — the
  `Hyper-V Administrators` membership and the helper's elevated role.
- [../configuration.md](../configuration.md), [../file-layout.md](../file-layout.md),
  [runtime-hyperv-helper.md](runtime-hyperv-helper.md).

## Sources

`src/CSweet.Office.Runtime.HyperV/CSweet.Office.Runtime.HyperV.csproj`,
`src/CSweet.Office.Runtime.HyperV/{HyperVIsolationBackend,HyperVSocketTransport,WindowsHyperVHostProbe,WindowsHyperVHostReadiness,WindowsHyperVFeatureProvisioner,WindowsRuntimeHostProvisioner,WindowsRuntimeHostProgressStore}.cs`,
`src/CSweet.Office.RuntimeHost/Program.cs`,
`tests/CSweet.Office.Tests/{WindowsHyperVOnboardingTests,HyperVInstanceReapingTests,PlatformIsolationBackendTests}.cs`.

Verified: 2026-09-15.
