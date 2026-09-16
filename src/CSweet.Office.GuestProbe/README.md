# CSweet.Office.GuestProbe

The in-guest probe used only by the isolation certification smoke test. It runs inside a freshly booted guest, collects twelve isolation facts about the VM it is running in, posts the report to the host over the guest broker's local socket, and exits non-zero if any check failed. It is never installed into a production guest image and has no project references.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/guest-probe.md`](../../docs/80-reference/projects/guest-probe.md).

**Output:** console exe
**References:** none

## What it owns

- `Program.cs` — the twelve checks evaluated into an ordered dictionary: Linux guest, artifact root, no network interface or default route, no host-filesystem mount, scratch mounted, outbound network denied, broker socket present, unprivileged workload, both guest executables present, and a 10.x SDK.
- `GuestProbeReport` — the posted report: suite `csweet-hardware-vm-smoke-v14`, pass flag, check dictionary, guest OS string, and completion time.
- `NativeMethods` — `GetEffectiveUserId`, used by the unprivileged-workload check.
- The hand-written HTTP POST to `POST /mcp` over the Unix socket named by `CSweet__Agent__McpUnixSocketPath`.

## Notes for contributors

- The csproj `<Description>` calls it "In-guest executable used only by the Hyper-V isolation certification smoke test"; in practice it is provider-agnostic — `WindowsSmokeTest` bundles the same probe binary for both the Hyper-V and Firecracker certification runs.
- An inspection failure is a conservative failure: I/O errors return the fail-closed value, so a guest that hides its own `/proc` state cannot pass.
- The report is the only output; there is no local log, and the host session is what turns it into certification evidence.
- The suite name is versioned and becomes the certification suite version that `CertifiedGuestImageRegistry` compares against.
- The HTTP request is hand-written because the endpoint is a Unix socket inside the guest; do not swap it for `HttpClient` without a socket-based handler.

## Tests

No unit test coverage in `tests/CSweet.Office.Tests`; the probe is exercised only by the certification smoke test, where its report is the first half of the evidence file.

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
