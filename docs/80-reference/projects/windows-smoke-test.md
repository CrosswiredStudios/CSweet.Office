# CSweet.Office.WindowsSmokeTest

The isolation certification harness. Despite the name it certifies both Windows and Linux: `--provider hyperv`
drives a real Hyper-V guest on Windows, `--provider firecracker` drives a real Firecracker guest on Linux. It
starts a real VM through the same privileged helper RuntimeHost uses, serves the host half of the guest broker
protocol, runs the in-guest probe and a builder smoke, and only then writes development certification
evidence. **It requires administrator (Windows) or root (Linux)**, and it contains **the only in-repo host-side
guest broker implementation, `CertificationBrokerHost.cs`, which is test-only** — production broker
authorization and streaming live in Headquarters.

## Project facts

| Fact | Value |
|---|---|
| Output kind | console exe (`Microsoft.NET.Sdk`, `<OutputType>Exe</OutputType>`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `Runtime.Abstractions`, `Runtime.Core`, `Runtime.Firecracker`, `Runtime.HyperV`, `Runtime.Protocol` |
| Key package references | none of its own |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.WindowsSmokeTest`) |
| `InternalsVisibleTo` | not set |
| csproj `<Description>` | "Runs real Hyper-V or Firecracker guests and emits development certification evidence only after isolation checks pass." |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `SmokeArguments` | record (internal) | `Provider` (default `hyperv`), `HelperExecutablePath`, `GuestImagePath`, `ProbeExecutablePath`, `OutputRoot`, `EvidenceOutputPath`, `PreserveFailedVirtualMachine`; `Parse`, `ValidatedOutputRoot`, `RequireFile`. |
| `CertificationReportHandler` | class (internal) | The `mcp.runtime` handler: deserializes the guest probe report from the request body and completes a `TaskCompletionSource`. |
| `BuilderCertificationHandler` | class (internal) | The builder handlers: serves the fixture archive and the NuGet metadata for `build.fetch`, reassembles and digest-verifies `build.artifact`, and tracks `build.progress`. |
| `SmokeGuestReport` | record (internal) | The guest probe report as received by the host. |
| `SmokeJson` | static class (internal) | The serializer options used for every smoke-test exchange. |
| `BrokerOperationContext`, `BrokerOperationResult` | records (internal, in `CertificationBrokerHost.cs`) | One proxied broker operation and its response. |
| `IAgentBrokerOperationHandler` | interface (internal) | `HandleAsync(BrokerOperationContext)`. |
| `AgentBrokerGrant` | record (internal) | The host-side capability grant: workload, channel and installation ids, image and artifact digests, protocol version, boot token, expiry, allowed purposes, and the four limits. `Validate` enforces all of them. |
| `GuestBrokerHostSession` | class (internal) | The broker session state machine: writes the boot configuration, verifies hello and proof, issues the lease, sends the start command, then answers proxy requests and health messages until the guest reports exit. |

## Entry points / composition

`Program.cs` runs a fixed sequence:

1. Refuse to proceed unless the provider matches the OS and the process is elevated — `WindowsPrincipal.IsInRole(Administrator)` for Hyper-V, `Environment.UserName == "root"` for Firecracker. Both failures throw.
2. Validate the output root, create `artifact-media` and a helper data root, and set `CSWEET_ARTIFACT_MEDIA_ROOT` plus `CSWEET_HYPERV_DATA_ROOT` or `CSWEET_FIRECRACKER_DATA_ROOT`.
3. For Hyper-V: derive the service id from `HyperVSocketTransportOptions` (120-second connect timeout), publish `CSWEET_HYPERV_BROKER_SERVICE_ID`, and create the registry key, deleting the legacy braced key first.
4. Resolve the descriptor from `IsolationProviderCatalog`, require the guest image (`.vhdx` or `.ext4`), helper, and probe files, hash the guest image, and build a probe bundle `certification-probe.csab`, write it as ISO media with `SingleFileIso9660`, and verify it.
5. **Runtime phase** — create a `RuntimeWorkloadSpecification` with fixed limits (1 vCPU, 100 %, 1 GiB memory, 1 GiB writable disk, 64 processes, 1 MiB logs, 3 minutes), a five-minute lease, a random 32-byte boot token, and entrypoint `probe`; invoke the helper `create` and `start`; connect the guest channel; run a `GuestBrokerHostSession` with grant purpose `mcp.runtime`, 8 requests maximum, 1 MiB request/response bodies, and 16 MiB frames; wait for the guest report and require every check to pass.
6. **Builder phase** — repeat with a `BuilderWorkloadSpecification` (1 vCPU, 100 %, 4 GiB memory, 1 GiB disk, 128 processes, 4 MiB logs, 5 minutes), grant purposes `build.fetch`, `build.artifact`, `build.progress`, 10 000 requests, and a `StartCommand` that runs `/usr/lib/csweet/builder/CSweet.Office.BuilderGuest` against an in-memory fixture archive. The returned bundle must contain `artifact.json` and an executable `payload/SmokeAgent`.
7. **Cleanup** — unless `--preserve-failed-vm` was set and certification did not succeed, stop and destroy the VM. A cleanup failure aborts before evidence is written.
8. **Evidence** — write the evidence JSON to `--evidence` and print a one-line summary.

The evidence document contains `providerId`, `providerVersion`, `hostOperatingSystem`, `hostArchitecture`,
`guestImageDigest`, `brokerProtocolVersion` `"1.0"`, `certificationSuiteVersion` (the guest report's suite),
`certifiedAt`, `certificationExpiresAt` (seven days later), the combined check dictionary, the guest OS
string, and `completedAt`.

## Behaviour worth knowing

- **Evidence is emitted only after every check passes.** The guest report must pass all twelve checks, the
  builder phase must pass its six checks, and the VM teardown must have succeeded. Any failure throws before
  the evidence file is written. This is the property that makes the file usable as certification input.
- **The evidence is development evidence.** It is what the release and provisioning scripts consume to
  populate the provider certification settings RuntimeHost verifies; it is not a signature.
- **`CertificationBrokerHost.cs` is the only host-side broker in this repository and it is test-only.** The
  file's own comment states that production broker authorization and streaming remain in the C-Sweet
  headquarters repository. `AgentBrokerGrant.Validate` re-checks identity, protocol version, boot-token
  length, expiry, digests, purpose syntax, and every numeric limit before a session starts;
  `GuestBrokerHostSession` then enforces per-request bounds: a request counter that answers
  `429 request-limit-exceeded`, a capability check that answers `403 capability-denied` for an unknown
  purpose, method, path, header, or oversized body, a 32-request concurrency cap, and a `502
  broker-operation-failed` fallback when a handler throws. Handler responses are re-validated for status code,
  size, and header shape before they are sent.
- **The session order is fixed**: boot configuration out, hello in, challenge out, proof in, lease out, start
  command out, then proxy and health traffic until the guest sends `Exit` (a non-zero exit fails the run).
- **The Hyper-V path opens the channel natively.** `ConnectGuestAsync` uses
  `WindowsHyperVSocketTransport.ConnectAsync(virtualMachineId)` for Hyper-V and
  `FirecrackerGuestChannelConnector` for Linux, so the harness exercises the same route RuntimeHost uses.
- **Fixture data is generated in memory.** The builder phase writes a ZIP containing `csweet-plugin.json`, a
  minimal `SmokeAgent.csproj` targeting `net10.0`, and a `Program.cs`; the NuGet handler answers the real
  service index and repository-signatures URLs with minimal documents, including one valid SHA-256 fingerprint,
  and rejects any other fetch with `UnauthorizedAccessException`.
- **The artifact upload contract is enforced strictly.** `ReceiveArtifactAsync` requires a contiguous sequence
  beginning at zero, a parseable completed flag, and — on the final chunk — a digest header that matches the
  accumulated SHA-256.
- **Argument validation is strict.** Paths must be absolute, the output root cannot be a file-system root, the
  guest image and helper extensions are checked (`.vhdx`/`.exe` for Hyper-V, `.ext4` otherwise), and only
  `hyperv` and `firecracker` are accepted as providers.
- **`--preserve-failed-vm true` leaves the VM and prints the provider instance id and data root** so an
  operator can inspect a failed certification attempt instead of losing the evidence.

> **Out of repo:** the production guest broker, the boot-configuration content that a real assignment sends,
> and the release-time certification pipeline that consumes the evidence file are Headquarters and release
> concerns. What is in this repository is a harness, and it is not a substitute for production broker review.

## Related tests

There is no unit-test class for this project; the test project does not reference it. Its logic is exercised
by running it, which requires administrator or root plus a provisioned guest image. The shared pieces it
depends on are covered indirectly: `HyperVInstanceReapingTests`, `FirecrackerHelperSecurityTests`, and
`ExternalPlatformStdioGuestChannelConnectorTests`.

## Related documentation

- [30-workloads/04-guest-broker-protocol.md](../../30-workloads/04-guest-broker-protocol.md) — the protocol the
  session implements, and the page that names this project as the in-repo reference implementation.
- [20-security/08-provider-certification.md](../../20-security/08-provider-certification.md) — how the evidence
  file becomes provider certification settings.
- [10-system/03-components.md](../../10-system/03-components.md) — the certification and test executables.
- [../../60-release/03-certification.md](../../60-release/03-certification.md) — the release-time process that
  invokes this harness.
- [guest-probe.md](guest-probe.md) — the guest half.

## Sources

`src/CSweet.Office.WindowsSmokeTest/CSweet.Office.WindowsSmokeTest.csproj`,
`src/CSweet.Office.WindowsSmokeTest/{Program.cs,CertificationBrokerHost.cs}`,
`tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj`.

Verified: 2026-09-15.
