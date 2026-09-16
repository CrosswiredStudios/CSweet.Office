# CSweet.Office.Runtime.AppleVirtualization

The macOS provider library. It contains the Apple Virtualization.framework backend that plugs into
RuntimeHost and the stdio guest-channel connector that tunnels the guest broker through the Swift helper. All
framework work happens in the helper; this library is a thin, complete configuration surface plus the
platform gate.

## Project facts

| Fact | Value |
|---|---|
| Output kind | library (`Microsoft.NET.Sdk`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `Runtime.Core` |
| Key package references | none of its own |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.Runtime.AppleVirtualization`) |
| `InternalsVisibleTo` | not set |
| csproj `<Description>` | "Certified Apple Virtualization.framework isolation backend." |

## Key types

The entire project is one file, `AppleVirtualizationIsolationBackend.cs`, with three types:

| Type | Kind | Responsibility |
|---|---|---|
| `AppleVirtualizationIsolationBackendOptions` | class | Subclass of `PlatformIsolationBackendOptions` that sets `RequiredGuestChannelTransport` to `ExternalPlatformStdioGuestChannelConnector.TransportName` (`stdio-duplex-v1`) in its constructor. |
| `AppleVirtualizationIsolationBackend` | class | `ExternalPlatformIsolationBackend` + `IPlatformWorkloadReaper`; `IsHostPlatform` requires macOS and otherwise reports "Virtualization.framework requires a macOS host." |
| `AppleVirtualizationGuestChannelConnector` | class | A subclass of `ExternalPlatformStdioGuestChannelConnector` bound to the `apple-virtualization` provider id. |

## Entry points / composition

No executable. `CSweet.Office.RuntimeHost.Program.cs` registers, on macOS only, the options singleton, the
backend as `IPlatformIsolationBackend`, and `AppleVirtualizationGuestChannelConnector` as
`IPlatformGuestChannelConnector`. Before that it calls
`PlatformRuntimePayloadManifest.ApplyIfConfigured(apple, IsolationProviderCatalog.AppleVirtualization())`, so
the installed payload manifest supplies the helper path and digest, guest image path and digest, signature and
pinned certificate, certification metadata, and broker protocol version.

## Behaviour worth knowing

- **The helper is invoked, never linked.** The Swift executable is built by SwiftPM (not by the .NET SDK) and
  is launched per invocation with `--protocol 1.0 --operation <op>`, exactly like the two C# helpers. The
  library's only responsibility is to verify the helper digest and the payload identity before starting it.
- **Everything the backend does is inherited gating.** Probe verifies helper digest, guest image digest,
  certification evidence digest and binding, and the detached image signature; create additionally verifies
  the artifact ISO by re-reading its ISO-9660 structures; every lifecycle call uses the handle stored in the
  authorization gate's ledger.
- **`stdio-duplex-v1` is mandatory.** Both the options class and the connector demand the same transport name
  the helper advertises, so a helper that speaks a different transport is reported unavailable rather than
  silently downgraded.
- **Reaping goes through `IPlatformWorkloadReaper`.** The backend's reap call reaches the helper's `reap`
  operation, which only considers runtime workloads (`kind == 1`); builder and toolchain instances are not
  reclaimed by the Apple helper. That asymmetry is visible in
  [30-workloads/07-provider-backends.md](../../30-workloads/07-provider-backends.md).
- **No Hyper-V-style native channel exists here.** The helper opens the `VZVirtioSocketDevice` connection and
  passes the descriptor back through its own stdio handshake; RuntimeHost never touches a socket device.

## Related tests

There is no test class dedicated to this project. `CertifiedGuestImageRegistryTests` and
`IsolationProviderSelectorTests` exercise the catalog descriptor for `apple-virtualization` indirectly
through the selector; the helper's own logic is Swift and is not covered by `tests/CSweet.Office.Tests`.

## Related documentation

- [30-workloads/07-provider-backends.md](../../30-workloads/07-provider-backends.md) — the macOS backend
  mechanics and the kind-1-only reaper asymmetry.
- [30-workloads/06-guest-images.md](../../30-workloads/06-guest-images.md) — the macOS guest image shape.
- [20-security/09-helper-protocol.md](../../20-security/09-helper-protocol.md) — the protocol the Swift helper
  hand-implements.
- [runtime-apple-virtualization-helper.md](runtime-apple-virtualization-helper.md) — the Swift package.

## Sources

`src/CSweet.Office.Runtime.AppleVirtualization/CSweet.Office.Runtime.AppleVirtualization.csproj`,
`src/CSweet.Office.Runtime.AppleVirtualization/AppleVirtualizationIsolationBackend.cs`,
`src/CSweet.Office.RuntimeHost/Program.cs`, `docs/30-workloads/07-provider-backends.md`.

Verified: 2026-09-15.
