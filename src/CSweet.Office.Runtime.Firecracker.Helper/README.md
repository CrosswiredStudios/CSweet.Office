# CSweet.Office.Runtime.Firecracker.Helper

The privileged Firecracker/jailer lifecycle helper. Like the Hyper-V helper it is a short-lived console process driven over stdio with `--protocol 1.0 --operation <op>` and one typed JSON request, with one extra mode: `open-guest-channel` answers, then stays alive as a transparent byte relay between its own standard streams and the guest's vsock connection. It is a privileged boundary; the operation surface is deliberately narrow.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/runtime-firecracker-helper.md`](../../docs/80-reference/projects/runtime-firecracker-helper.md).

**Output:** console exe
**References:** `CSweet.Office.Runtime.Core`, `CSweet.Office.Runtime.Firecracker`

## What it owns

- `FirecrackerHelperController` — the eight lifecycle operations plus `OpenGuestChannelAsync`, instance metadata, jail staging, cgroup limits, and process-ownership checks.
- `FirecrackerApiClient` — the hand-built HTTP client bound to the jail's Firecracker API socket (`/logger`, `/machine-config`, `/boot-source`, `/drives`, `/vsock`).
- `FirecrackerHelperPaths` — environment-driven roots, jailer and firecracker executables, kernel and initrd, workload uid/gid, and guest vsock port, with fixed defaults and containment checks.
- `HelperArguments` — accepts only `--protocol` and `--operation`.
- `UnixOwnership` and `UnixSignal` — the P/Invoke wrappers for `chown` and `kill`.

## Notes for contributors

- The jailer argument list (`BuildJailerArguments`) is built in one testable method and asserted by `FirecrackerHelperSecurityTests`: namespaces and hard resource limits, no networking. Do not add jail network options.
- `open-guest-channel` uses a different read mode from every other operation: one newline-terminated request, a response line, then binary relay on the same streams.
- The process always exits `0` on typed failures, for the same reason as the Hyper-V helper; do not "fix" the exit code.
- Instance identity is verified against `instance.json` and `/proc/<pid>` ownership before any lifecycle call acts.
- The operation set is fixed (SEC-INV-20); the probe is a checklist (cgroup v2, trusted executables and kernel, root-only data root, openable `/dev/kvm`, matching versions), not a version comparison.

## Tests

`FirecrackerHelperSecurityTests` (operation surface, jailer arguments, traversal rejection, vsock handshake bounds), `ExternalPlatformStdioGuestChannelConnectorTests` (the client side of the handshake), and `HyperVInstanceReapingTests` (the mirrored reap predicate).

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
