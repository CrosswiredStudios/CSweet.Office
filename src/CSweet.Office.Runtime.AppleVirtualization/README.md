# CSweet.Office.Runtime.AppleVirtualization

The macOS provider library: the Apple Virtualization.framework backend that plugs into RuntimeHost and the stdio guest-channel connector that tunnels the guest broker through the Swift helper. All framework work happens in the helper; this library is a thin, complete configuration surface plus the platform gate.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/runtime-apple-virtualization.md`](../../docs/80-reference/projects/runtime-apple-virtualization.md).

**Output:** library
**References:** `CSweet.Office.Runtime.Core`

## What it owns

The entire project is one file, `AppleVirtualizationIsolationBackend.cs`:

- `AppleVirtualizationIsolationBackend` — derives from `ExternalPlatformIsolationBackend`, re-exposes reaping through `IPlatformWorkloadReaper`, and requires a macOS host.
- `AppleVirtualizationIsolationBackendOptions` — pins `RequiredGuestChannelTransport` to `stdio-duplex-v1`.
- `AppleVirtualizationGuestChannelConnector` — the `ExternalPlatformStdioGuestChannelConnector` subclass bound to the `apple-virtualization` provider id.

## Notes for contributors

- The Swift helper is invoked as a process and never linked; the library's only job is verifying the helper digest and payload identity before starting it.
- The Apple helper's `reap` considers only kind-1 (runtime) instances — the reaper asymmetry is documented in [docs/30-workloads/07-provider-backends.md](../../docs/30-workloads/07-provider-backends.md). Do not assume builder or toolchain cleanup happens here.
- Everything else is inherited gating from `ExternalPlatformIsolationBackend`; an unavailable or uncertified provider fails closed (SEC-INV-10).
- No Swift code is exercised by the .NET test suite.

## Tests

No dedicated test class in `tests/CSweet.Office.Tests`. The catalog descriptor is exercised indirectly by `IsolationProviderSelectorTests` and `CertifiedGuestImageRegistryTests`.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
