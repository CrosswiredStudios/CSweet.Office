# CSweet.Office.Runtime.AppleVirtualization.Helper

The macOS helper: a Swift package, not a .NET project, built with SwiftPM (`swift-tools-version: 5.9`, `platforms: [.macOS(.v14)]`) against the Virtualization.framework. It hand-implements the same privileged-helper contract as the two C# helpers — `--protocol 1.0 --operation <op>`, one typed JSON request, one typed JSON response — and doubles as the long-lived per-VM `--workload-host` process that owns the `VZVirtualMachine`. It is a privileged boundary; the operation surface is deliberately narrow.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/runtime-apple-virtualization-helper.md`](../../docs/80-reference/projects/runtime-apple-virtualization-helper.md).

**Output:** Swift executable
**References:** none

## What it owns

All sources live in `Sources/CSweetAppleVirtualizationHelper/`:

- `main.swift` — process entry: dispatches CLI operations or runs the workload host, and relays stdio to the vsock for `open-guest-channel`.
- `Models.swift` — the wire model (`providerID = "apple-virtualization"`, `guestChannelTransport = "stdio-duplex-v1"`, `helperProtocolVersion = "1.0"`), request/response types, and the ISO-8601 coders.
- `SecureIO.swift` — protected directories, bounded reads, manager-socket helpers, the poll-based relay loop, and `verifyArtifactISO`, an independent ISO-9660 re-parse and hash.
- `VirtualMachineManager.swift` — the long-lived workload host: builds the VM configuration, listens on a token-authenticated manager socket, and records terminal state.
- `HelperController.swift` — the CLI operations (`probe`, `create`, `start`, `inspect`, `stop`, `destroy`, `logs`, `reap`) against the manager.

## Notes for contributors

- `CSweet.AppleVirtualization.entitlements` carries `com.apple.security.virtualization` set to `true`; the macOS installer verifies it, and `new-installer-package.sh` refuses a helper without it.
- Two processes per VM: `create` spawns the same binary as `--workload-host`, which owns the VM and a Unix socket under the socket root; later lifecycle calls are token-authenticated JSON lines.
- `reap` is deliberately narrow (kind 1 only); builder and toolchain instances are left to their owners.
- Error codes mirror the C# helper vocabulary, and `ResourceLimits.validate()` bounds every numeric field; keep the two implementations in step.
- The package is absent from both solutions and is built and signed by the release scripts, not by `dotnet build`.

## Tests

No unit test coverage in `tests/CSweet.Office.Tests`; the Swift sources are not compiled or loaded by any test. The .NET-side contract assertions live in `ExternalPlatformStdioGuestChannelConnectorTests` and `FirecrackerHelperSecurityTests`.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
