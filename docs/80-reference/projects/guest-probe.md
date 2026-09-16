# CSweet.Office.GuestProbe

The in-guest probe used only by the isolation certification smoke test. It runs inside a freshly booted guest,
collects twelve isolation facts about the VM it is running in, posts the report to the host over the guest
broker's local socket, and exits non-zero if any check failed. It is never installed into a production guest
image. It has no project references.

## Project facts

| Fact | Value |
|---|---|
| Output kind | console exe (`Microsoft.NET.Sdk`, `<OutputType>Exe</OutputType>`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | none |
| Key package references | none of its own |
| `AssemblyName` / `RootNamespace` | not set (assembly `CSweet.Office.GuestProbe`) |
| `InternalsVisibleTo` | not set |
| csproj `<Description>` | "In-guest executable used only by the Hyper-V isolation certification smoke test." |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `GuestProbeReport` | record (internal) | The posted report: suite name, `Passed`, the check dictionary, guest OS string, and completion time. |
| `NativeMethods` | static class (internal) | `GetEffectiveUserId`, used for the unprivileged-workload check. |

`Program.cs` is top-level statements; there is no service, no host, and no configuration.

## Entry points / composition

The executable performs one pass and exits:

1. Evaluate twelve checks into an ordered dictionary.
2. Build a `GuestProbeReport` with suite `csweet-hardware-vm-smoke-v14` and `Passed` set to "every check is
   true".
3. `PostReportAsync`: read the socket path from `CSweet__Agent__McpUnixSocketPath`, connect a Unix socket, and
   send a raw HTTP/1.1 `POST /mcp` with the JSON body and `Connection: close`; a response that is not
   `HTTP/1.1 200` throws.
4. Return 0 when `Passed`, otherwise 2.

## Behaviour worth knowing

The twelve checks are the guest-side half of the certification claim. Each is a positive assertion about the
running VM:

| Check key | Assertion |
|---|---|
| `linux-guest` | The probe is running on Linux. |
| `artifact-root` | The current directory is beneath `/run/csweet/artifact/payload`. |
| `no-network-interface` | `/sys/class/net` contains only `lo`. |
| `no-default-route` | `/proc/net/route` has no default route. |
| `no-host-filesystem-mount` | `/proc/self/mountinfo` contains no 9p, CIFS, SMB3, NFS, vboxsf, hgfs, drvfs, or `fuse.sshfs` mount. |
| `ephemeral-scratch-mounted` | A mount point at `/run/csweet` exists. |
| `outbound-network-denied` | A TCP connect to `1.1.1.1:443` fails or times out within two seconds. |
| `broker-socket-present` | The socket named by `CSweet__Agent__McpUnixSocketPath` exists. |
| `workload-unprivileged` | The effective user id is not 0. |
| `builder-executable-present` | `/usr/lib/csweet/builder/CSweet.Office.BuilderGuest` exists in the image. |
| `toolchain-executable-present` | `/usr/lib/csweet/toolchain/CSweet.Office.ToolchainGuest` exists in the image. |
| `dotnet-sdk-present` | `/usr/bin/dotnet --list-sdks` lists a 10.x SDK. |

- **An inspection failure is a failed check, not an exception.** `NetworkInterfaces`, `HasDefaultRoute`,
  `HasForbiddenMount`, and `IsScratchMounted` catch IO failures and return the conservative value; a missing
  `/proc` file is treated as present (`HasDefaultRoute` returns true and so on), so a guest that hides its
  own state cannot pass.
- **The report is the only output.** There is no local log, no file, and no exit-code channel other than
  pass/fail; the host session is what turns the report into certification evidence.
- **The suite name is versioned.** `csweet-hardware-vm-smoke-v14` is written into the evidence as the
  certification suite version, which is what `CertifiedGuestImageRegistry` compares against a required
  version.
- **The HTTP request is hand-written.** The probe does not use `HttpClient` because the endpoint is a Unix
  socket inside the guest; it writes the request line, headers, and body directly and checks the status line.
- **The probe is not part of any product image.** It is copied into the smoke test's artifact media as a tar
  bundle with entrypoint `probe` and never appears in a guest-image provision script.

> **Out of repo:** the certification broker that receives this report in production is Headquarters code. The
> in-repo reference implementation, `CertificationBrokerHost.cs`, is test-only and lives in
> [`WindowsSmokeTest`](windows-smoke-test.md).

## Related tests

There is no test class for this project. Its behaviour is exercised only by the isolation certification smoke
test, where its report is the first half of the evidence file.

## Related documentation

- [30-workloads/04-guest-broker-protocol.md](../../30-workloads/04-guest-broker-protocol.md) — the local
  broker endpoint the report is posted to.
- [20-security/08-provider-certification.md](../../20-security/08-provider-certification.md) — how these
  checks become certification evidence.
- [30-workloads/06-guest-images.md](../../30-workloads/06-guest-images.md) — what a certified guest image
  contains.
- [windows-smoke-test.md](windows-smoke-test.md) — the host side that runs this probe.

## Sources

`src/CSweet.Office.GuestProbe/CSweet.Office.GuestProbe.csproj`, `src/CSweet.Office.GuestProbe/Program.cs`,
`src/CSweet.Office.WindowsSmokeTest/Program.cs`.

Verified: 2026-09-15.
