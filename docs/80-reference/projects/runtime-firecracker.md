# CSweet.Office.Runtime.Firecracker

The Linux provider library: the Firecracker backend that plugs into RuntimeHost and the stdio guest-channel
connector that tunnels the guest broker through the privileged helper. There is no native Firecracker socket in
this library — unlike Hyper-V, the Linux path always goes through the helper's standard streams. It derives
from `ExternalPlatformIsolationBackend`.

## Project facts

| Fact | Value |
|---|---|
| Output kind | library (`Microsoft.NET.Sdk`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `Runtime.Core` |
| Key package references | none of its own |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.Runtime.Firecracker`) |
| `InternalsVisibleTo` | not set |
| csproj `<Description>` | "Certified Firecracker/KVM isolation backend." |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `FirecrackerIsolationBackendOptions` | class | Subclass of `PlatformIsolationBackendOptions` that sets `RequiredGuestChannelTransport` to `ExternalPlatformStdioGuestChannelConnector.TransportName` (`stdio-duplex-v1`) in its constructor. |
| `FirecrackerIsolationBackend` | class | `ExternalPlatformIsolationBackend` + `IPlatformWorkloadReaper`; `IsHostPlatform` requires Linux. |
| `FirecrackerGuestChannelConnector` | class | A two-line subclass of `ExternalPlatformStdioGuestChannelConnector` bound to the `firecracker-kvm` provider id. |

All three types live in `FirecrackerIsolationBackend.cs`.

## Entry points / composition

No executable. `CSweet.Office.RuntimeHost.Program.cs` registers, on Linux only, the options singleton, the
backend as `IPlatformIsolationBackend`, and `FirecrackerGuestChannelConnector` as
`IPlatformGuestChannelConnector`. Before that, it calls
`PlatformRuntimePayloadManifest.ApplyIfConfigured(firecracker, IsolationProviderCatalog.Firecracker())`, so a
configured payload manifest replaces the helper path, digests, certification metadata, and broker protocol
version with values verified against the installed package. `WindowsSmokeTest` constructs the same connector
directly for the Linux certification path.

## Behaviour worth knowing

- **The connector requires the certified transport name.** `OpenGuestChannelAsync` fails with
  `IsolationUnavailableException` unless `RequiredGuestChannelTransport` is exactly `stdio-duplex-v1`, then
  re-verifies the helper digest (SHA-256, lowercase, constant-time compare) before starting the helper with
  `--operation open-guest-channel`.
- **Framing is one line, then opaque bytes.** The request is JSON followed by `\n`; the connector reads
  exactly one bounded JSON line (4 KiB maximum, no NUL, no `\r`) and validates
  `guestChannelTransport == stdio-duplex-v1`. Everything after that newline is guest broker traffic, relayed
  through a private duplex `Stream` wrapper that kills the helper process tree on dispose.
- **The handshake reader is shared.** `ExternalPlatformStdioGuestChannelConnector.ReadHandshakeAsync` is
  `internal static` and is asserted directly by the test suite, because a reader that consumed one byte too
  many would corrupt the first guest frame.
- **Linux only.** `IsHostPlatform` returns false with "Firecracker/KVM requires a Linux host." on any other
  OS, so the probe reports unavailable rather than throwing.
- **Reaping is exposed by explicit interface implementation**, matching Hyper-V; RuntimeHost's reaper resolves
  `IPlatformWorkloadReaper` from the registered backend.
- **The helper path and digest come from configuration or the payload manifest**, never from the control
  plane. A workload cannot influence which executable is started.

## Related tests

| Test class | What it pins |
|---|---|
| `ExternalPlatformStdioGuestChannelConnectorTests` | That the handshake reader stops at the newline without consuming broker bytes, that oversized or ambiguous framing is rejected, and that the connector rejects a wrong provider and control characters in the instance id. |
| `PlatformIsolationBackendTests` | That the Firecracker backend probe fails closed off Linux or without an installed helper. |
| `FirecrackerHelperSecurityTests` | The shared helper-contract assertions for the executable this library starts. |
| `RuntimeHostRpcIntegrationTests` | That a provider without a guest-channel connector cannot be probed as available or used to create a workload. |

## Related documentation

- [30-workloads/07-provider-backends.md](../../30-workloads/07-provider-backends.md) — Firecracker create,
  start, log, and reap mechanics.
- [20-security/09-helper-protocol.md](../../20-security/09-helper-protocol.md) — the stdio protocol.
- [10-system/03-components.md](../../10-system/03-components.md) — the guest-channel table across platforms.
- [runtime-firecracker-helper.md](runtime-firecracker-helper.md) — the privileged executable.

## Sources

`src/CSweet.Office.Runtime.Firecracker/CSweet.Office.Runtime.Firecracker.csproj`,
`src/CSweet.Office.Runtime.Firecracker/FirecrackerIsolationBackend.cs`,
`src/CSweet.Office.Runtime.Core/ExternalPlatformStdioGuestChannelConnector.cs`,
`src/CSweet.Office.RuntimeHost/Program.cs`,
`tests/CSweet.Office.Tests/{ExternalPlatformStdioGuestChannelConnectorTests,PlatformIsolationBackendTests,FirecrackerHelperSecurityTests}.cs`.

Verified: 2026-09-15.
