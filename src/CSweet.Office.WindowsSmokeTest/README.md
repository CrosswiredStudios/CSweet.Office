# CSweet.Office.WindowsSmokeTest

The isolation certification harness. Despite the name it certifies both Windows and Linux: `--provider hyperv` drives a real Hyper-V guest on Windows, `--provider firecracker` drives a real Firecracker guest on Linux. It requires administrator (Windows) or root (Linux), and it emits development certification evidence only after every check passes. It contains the only in-repo host-side guest broker implementation, `CertificationBrokerHost.cs`, which is test-only.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/windows-smoke-test.md`](../../docs/80-reference/projects/windows-smoke-test.md).

**Output:** console exe
**References:** `CSweet.Office.Runtime.Abstractions`, `CSweet.Office.Runtime.Core`, `CSweet.Office.Runtime.Firecracker`, `CSweet.Office.Runtime.HyperV`, `CSweet.Office.Runtime.Protocol`

## What it owns

- `Program.cs` — the fixed run sequence: elevation gate, output root and image validation, probe media generation, runtime phase, builder phase, cleanup, and evidence emission.
- `SmokeArguments` — provider, helper, guest image, probe, output root, evidence path, and `--preserve-failed-vm`.
- `CertificationBrokerHost.cs` — `GuestBrokerHostSession`, `AgentBrokerGrant`, and the handler interfaces: the host half of the guest broker protocol, test-only.
- `CertificationReportHandler` and `BuilderCertificationHandler` — the runtime probe report and the builder fixture serving, artifact reassembly, and progress tracking.
- `SmokeGuestReport` and `SmokeJson` — the received report and the serializer options for every exchange.

## Notes for contributors

- Evidence is written only after the guest checks, the builder checks, and VM teardown all succeed; a cleanup failure aborts before evidence.
- `CertificationBrokerHost.cs` is test-only and is not the production broker; production broker authorization and streaming live outside this repository. Do not treat the file as the production implementation.
- The harness exercises the same guest-channel route RuntimeHost uses: the native `AF_HYPERV` socket on Windows, the stdio connector on Linux.
- `--preserve-failed-vm true` leaves the VM in place and prints the provider instance id and data root for inspection.
- Argument validation is strict: absolute paths only, `hyperv` and `firecracker` only, and guest-image and helper extensions are checked per provider.

## Tests

No unit test coverage in `tests/CSweet.Office.Tests`; the harness is exercised by running it, which requires administrator or root plus a provisioned guest image. Its shared pieces are covered indirectly by `HyperVInstanceReapingTests`, `FirecrackerHelperSecurityTests`, and `ExternalPlatformStdioGuestChannelConnectorTests`.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
