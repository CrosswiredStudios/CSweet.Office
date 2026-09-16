# CSweet.Office.Runtime.HyperV

The Windows provider library: the Hyper-V backend that plugs into RuntimeHost, the hand-rolled `AF_HYPERV` socket transport that opens the guest broker channel, the host readiness probe, the optional-feature provisioner, and the elevated RuntimeHost install/repair launcher with its progress store. The VM lifecycle itself happens in the privileged helper; this library owns everything around it.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/runtime-hyperv.md`](../../docs/80-reference/projects/runtime-hyperv.md).

**Output:** library
**References:** `CSweet.Office.Runtime.Core`

## What it owns

- `HyperVIsolationBackend` — derives from `ExternalPlatformIsolationBackend`, re-exposes reaping through `IPlatformWorkloadReaper`, and requires a Windows host.
- `WindowsHyperVSocketTransport` — the raw `AF_HYPERV` socket (address family 34, protocol 1, a 36-byte `SOCKADDR_HV`) and the `IPlatformGuestChannelConnector` the server splices to.
- `WindowsHyperVHostProbe` and `WindowsHyperVHostReadiness` — registry edition checks, DISM feature state, processor and memory requirements, cached for 30 seconds.
- `WindowsHyperVFeatureProvisioner` and `WindowsRuntimeHostProvisioner` — elevated `dism` enablement and packaged-installer, developer-bootstrap, or access-repair launches.
- `WindowsRuntimeHostProgressStore` — validates the untrusted `windows-isolation-*.json` progress documents under `%PROGRAMDATA%\CSweet\Setup`.

## Notes for contributors

- The managed `ConnectAsync`/`ConnectEx` path rejects the non-IP address family with `WSAEINVAL`; the socket code is hand-rolled and polls short non-blocking attempts for that reason. Do not "simplify" it back to the managed API.
- On Windows the guest channel is opened by RuntimeHost, not the helper; the Linux and macOS backends tunnel through helper stdio instead.
- Provisioning never runs a command line supplied by Headquarters (SEC-INV-19), and the staged enrollment token is a file, not an argument (SEC-INV-21).
- Progress documents are untrusted input; keep the size, schema, timestamp, and reparse-point checks.
- `HyperVSocketTransportOptions.ServiceId` is derived from `LinuxVsockPort`, so a port change changes the registry key the guest must listen under.

## Tests

`WindowsHyperVOnboardingTests` (a mix of behavioural tests and script-text assertions), `HyperVInstanceReapingTests`, `PlatformIsolationBackendTests`, and the probe-gating coverage in `RuntimeHostRpcIntegrationTests`.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
