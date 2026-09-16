# CSweet.Office.Runtime.Firecracker

The Linux provider library: the Firecracker/KVM backend that plugs into RuntimeHost and the stdio guest-channel connector that tunnels the guest broker through the privileged helper. There is no native socket in this library; the Linux path always goes through the helper's standard streams.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/runtime-firecracker.md`](../../docs/80-reference/projects/runtime-firecracker.md).

**Output:** library
**References:** `CSweet.Office.Runtime.Core`

## What it owns

All three types live in `FirecrackerIsolationBackend.cs`:

- `FirecrackerIsolationBackend` — derives from `ExternalPlatformIsolationBackend`, re-exposes reaping through `IPlatformWorkloadReaper`, and requires a Linux host.
- `FirecrackerIsolationBackendOptions` — pins `RequiredGuestChannelTransport` to `stdio-duplex-v1`.
- `FirecrackerGuestChannelConnector` — the `ExternalPlatformStdioGuestChannelConnector` subclass bound to the `firecracker-kvm` provider id.

## Notes for contributors

- The connector re-verifies the helper digest (constant-time compare) before every start and fails closed unless the transport is exactly `stdio-duplex-v1`.
- The handshake reader is shared with the Apple backend (`ExternalPlatformStdioGuestChannelConnector.ReadHandshakeAsync`, `internal static`) and is asserted directly by the tests; a reader that consumes one byte too many corrupts the first guest frame.
- The helper path and digest come from configuration or the payload manifest, never from the control plane; a workload cannot influence which executable starts.
- Off Linux the probe reports unavailable rather than throwing, and there is no fallback path (SEC-INV-10).

## Tests

`ExternalPlatformStdioGuestChannelConnectorTests` (handshake framing), `PlatformIsolationBackendTests` (fail-closed probe), `FirecrackerHelperSecurityTests` (the helper contract this library starts), and `RuntimeHostRpcIntegrationTests` (probe gating without a connector).

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
