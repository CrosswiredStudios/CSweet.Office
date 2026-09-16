# CSweet.Office.Runtime.AppleVirtualization.Helper

The macOS helper. **This is a Swift package, not a .NET project** — it is absent from both solutions and is
built with SwiftPM against the Virtualization.framework. It implements the same privileged-helper contract as
the two C# helpers by hand: `--protocol 1.0 --operation <op>` with one typed JSON request on standard input
and one typed JSON response on standard output, plus the `open-guest-channel` mode where the process stays
alive and relays bytes. It also contains a second personality: invoked as `--workload-host <metadata path>` it
becomes the long-lived per-VM manager process that owns the `VZVirtualMachine`.

## Project facts

| Fact | Value |
|---|---|
| Output kind | Swift package executable |
| Target framework | n/a; `Package.swift` declares `platforms: [.macOS(.v14)]` and `swift-tools-version: 5.9` |
| Project references | none; it re-implements the JSON-over-stdio contract rather than linking `Runtime.Core` |
| Products | `.executable(name: "CSweet.Office.Runtime.AppleVirtualization.Helper", targets: ["CSweetAppleVirtualizationHelper"])` |
| Target | `.executableTarget(name: "CSweetAppleVirtualizationHelper", path: "Sources/CSweetAppleVirtualizationHelper")` |
| Entitlements | `CSweet.AppleVirtualization.entitlements` with the single key `com.apple.security.virtualization` set to `true` |
| Solution membership | neither `CSweet.Office.slnx` nor `CSweet.Office.Independent.slnx` |
| Builder | SwiftPM; the release scripts build and sign it outside `dotnet build` |

## Swift source files

All sources live in `Sources/CSweetAppleVirtualizationHelper/`.

| File | Responsibility |
|---|---|
| `main.swift` | Process entry. When called as `--workload-host <metadata>` it runs `VirtualMachineManager(...).run()`; otherwise it parses `--protocol`/`--operation`, reads a bounded request (newline-terminated for `open-guest-channel`, to end-of-input otherwise), dispatches to `HelperController`, and for `open-guest-channel` writes the response line before relaying. Also installs `SIG_IGN` for `SIGPIPE`. |
| `Models.swift` | The wire model: `providerID = "apple-virtualization"`, `guestChannelTransport = "stdio-duplex-v1"`, `helperProtocolVersion = "1.0"`; `PlatformRequest` (optional `builderWorkload`/`runtimeWorkload`, `handle`, `guestImagePath`, `artifactImagePath`, `gracePeriodSeconds`, `maximumBytes`), `WorkloadSpec`, `ResourceLimits` with `validate()`, `BrokerLease`, `ArtifactReference`, `WorkloadHandle`, `WorkloadStatus`, `PlatformResponse` with `ok`/`failure` factories, `LogChunk`, `InstanceMetadata`, `ManagerRequest`, `HelperError`, and the custom ISO-8601 `JSONDecoder.csweet` / `JSONEncoder.csweet`. |
| `SecureIO.swift` | The privileged plumbing: `HelperPaths.resolve()` (data root, package root, artifact media root, socket root, guest port), `safeChild`, `ensureProtectedDirectory` (0700), `secureRandomToken()` (32 bytes from `SecRandomCopyBytes`), atomic metadata save and bounded load (1-65 536 bytes), `readBounded`, `writeResponse`, Unix listener creation and connection, `readSocketLine`, `sendAll`, `relaySplit`/`relayDuplex` (a `poll` loop with 64 KiB buffers), and `verifyArtifactISO`, which independently re-parses the ISO-9660 primary volume descriptor and directory record and hashes the payload extent with CryptoKit. |
| `VirtualMachineManager.swift` | The long-lived `--workload-host` process: refuses to run without euid 0, validates that its metadata paths stay inside the protected instance directory and the configured socket root, builds the `VZVirtualMachineConfiguration`, listens on the manager socket, authenticates each command with a constant-time token compare, and answers `start`, `inspect`, `stop`, `destroy`, and `open-guest-channel`. It is its own `VZVirtualMachineDelegate` and records a terminal error into the status. |
| `HelperController.swift` | The CLI operations against the manager: `probe` (checks `VZVirtualMachine.isSupported`, euid 0, protected directories, trusted kernel), `create` (validates resources and image extensions, checks artifact media path and digest, creates scratch, writes metadata, spawns the workload-host process, waits for its socket), `start`/`inspect`/`stop`/`destroy` (via the manager socket), `logs` (empty list), and `reap` (kind 1, finished or expired leases only). |

## Behaviour worth knowing

- **Two processes per VM.** `create` forks the helper binary as `--workload-host`; that child owns the VM and
  a Unix socket under the configured socket root, and every later lifecycle call is a token-authenticated JSON
  line to that socket. `destroy` unlinks the socket and deletes the instance directory.
- **The manager validates its own metadata before trusting it.** The workload host requires that
  `instance.json`, `scratch.raw`, the manager socket path, and the kernel path all resolve inside the
  protected roots, and that the manager token is 32-256 bytes.
- **The VM is network-free and boot-token driven.** `networkDevices = []`; storage is a read-only guest image,
  a writable scratch image, and an optional read-only artifact ISO. The Linux boot loader command line is
  `console=hvc0 panic=-1 reboot=t root=/dev/vda ro csweet.broker_port=<port>`.
- **`open-guest-channel` is a handshake plus a relay.** The helper connects to the manager, asks it for a
  `VZVirtioSocketDevice` connection at the broker port, writes the JSON response line carrying
  `guestChannelTransport = stdio-duplex-v1`, and then runs a `poll`-based relay between its own stdio and the
  vsock descriptor.
- **Limits mirror the .NET helpers.** `ResourceLimits.validate()` bounds vCPUs 1-64, CPU percent 1-6400,
  memory 128-1 048 576 MiB, writable disk 64-1 048 576 MiB, process count 1-1 000 000, and maximum log bytes
  1-1 073 741 824. Error codes use the same vocabulary as the C# helpers (`invalid-arguments`,
  `unsupported-protocol`, `invalid-workload`, `invalid-guest-image`, `invalid-artifact-media`,
  `unsupported-operation`, and so on).
- **Every path is refused if it escapes.** `safeChild` rejects absolute paths, empty, `.` and `..` segments,
  NUL bytes, and any candidate that does not end up beneath the normalized root; the instance path helpers use
  it unconditionally.
- **The artifact ISO verifier does not trust `SingleFileIso9660`'s output blindly.** It re-reads sector 16
  (PVD with `CD001`, version 1, root extent 20), sector 20 (directory records), and requires the artifact file
  name, extent 21, a positive byte count, and a SHA-256 match.
- **`reap` is deliberately narrow.** Only `kind == 1` instances that are finished or whose lease is absent or
  expired are destroyed; builder and toolchain instances are left for their owners.

> **Out of repo:** the package layout, code-signing identity, and `vmlinux` kernel used by the macOS helper
> come from the release pipeline; the helper contract itself is documented in this repository.

## Related tests

No Swift tests exist in this repository, and no .NET test loads the helper. The contract this package
implements is asserted on the .NET side by `ExternalPlatformStdioGuestChannelConnectorTests` (handshake
framing), `FirecrackerHelperSecurityTests` (operation surface and path rules, which the Swift helper mirrors),
and the `logs`/`probe` behaviours described in
[20-security/09-helper-protocol.md](../../20-security/09-helper-protocol.md).

## Related documentation

- [20-security/09-helper-protocol.md](../../20-security/09-helper-protocol.md) — the protocol implemented
  here, including why the Swift helper is the one hand-written implementation.
- [30-workloads/07-provider-backends.md](../../30-workloads/07-provider-backends.md) — the macOS lifecycle
  and manager-socket model.
- [10-system/03-components.md](../../10-system/03-components.md) — the platform table placing this helper.
- [runtime-apple-virtualization.md](runtime-apple-virtualization.md) — the .NET backend that starts it.

## Sources

`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Package.swift`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/CSweet.AppleVirtualization.entitlements`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Sources/CSweetAppleVirtualizationHelper/{main,Models,SecureIO,VirtualMachineManager,HelperController}.swift`,
`CSweet.Office.slnx`, `CSweet.Office.Independent.slnx`.

Verified: 2026-09-15.
