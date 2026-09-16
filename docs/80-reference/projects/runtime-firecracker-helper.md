# CSweet.Office.Runtime.Firecracker.Helper

The privileged Firecracker helper. Like the Hyper-V helper it is a short-lived console process driven over
stdio with `--protocol 1.0 --operation <op>` and one typed JSON request, but it has one extra operation —
`open-guest-channel` — where the process does not exit after answering: it writes the JSON response line and
then becomes a transparent relay between its own standard streams and the guest's vsock connection. It also
talks to the running Firecracker instance over the API socket with a hand-built Unix-domain HTTP client.

## Project facts

| Fact | Value |
|---|---|
| Output kind | console exe (`Microsoft.NET.Sdk`, `<OutputType>Exe</OutputType>`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `Runtime.Core`, `Runtime.Firecracker` |
| Key package references | none of its own |
| `AssemblyName` | `CSweet.Office.Runtime.Firecracker.Helper` (explicit) |
| `RootNamespace` | not set (defaults to the assembly name) |
| `InternalsVisibleTo` | `CSweet.Office.Tests` |
| csproj `<Description>` | "Privileged, narrow Firecracker/jailer lifecycle helper for C-Sweet RuntimeHost." |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `Program` | top-level statements | Two different read modes: for `open-guest-channel` it reads one newline-terminated request, answers, and bridges both directions; for every other operation it reads to end-of-input and writes one response. |
| `HelperArguments` | record (internal) | Same shape as the Hyper-V helper's: `--protocol` / `--operation` only. |
| `HelperProtocolException` | exception (internal) | Protocol error code plus message. |
| `FirecrackerHelperController` | class (internal) | The eight lifecycle operations plus `OpenGuestChannelAsync`, instance metadata, jail staging, cgroup limits, and process ownership checks. |
| `FirecrackerInstanceMetadata` | record (internal) | Instance id, workload id and kind, jail id and root, process id, guest CID, timestamps, lease expiry. |
| `GuestChannelOpenResult` | record (internal) | The response plus the optional connected stream. |
| `FirecrackerHelperPaths` | record (internal) | Data root, instances root, jailer root, artifact media root, firecracker and jailer executables, kernel and initrd paths, workload uid/gid, guest vsock port, parent cgroup. |
| `FirecrackerApiClient` | class (internal) | HTTP client bound to the jail's API socket: `PutAsync`, `GetInstanceStateAsync`, `ProbeAsync`. |
| `FirecrackerApiException` | exception (internal) | API failure with a bounded, sanitized body excerpt. |
| `UnixOwnership` / `UnixSignal` | static classes (internal) | P/Invoke wrappers for `chown` and `kill`. |

## Entry points / composition

`Program.cs`:

1. If the operation is `open-guest-channel`: read one newline-terminated request, call
   `controller.OpenGuestChannelAsync`, write the response with a trailing newline, then `BridgeAsync` the
   standard streams to the vsock socket until either direction finishes.
2. Otherwise: read the whole request with a 1 MiB ceiling, call `controller.ExecuteAsync`, write the response
   without a trailing newline.
3. Always `return 0`; protocol rejections are valid responses, and a non-zero exit would make RuntimeHost
   discard the typed error.

`FirecrackerHelperPaths.Resolve()` reads environment configuration with fixed defaults:
`CSWEET_FIRECRACKER_DATA_ROOT` (`/var/lib/csweet/office/firecracker`),
`CSWEET_FIRECRACKER_PACKAGE_ROOT` (`/opt/csweet/office/firecracker`),
`CSWEET_ARTIFACT_MEDIA_ROOT` (`/var/lib/csweet/artifact-media`),
`CSWEET_FIRECRACKER_WORKLOAD_UID`/`_GID` (65534), `CSWEET_FIRECRACKER_GUEST_VSOCK_PORT` (5000), and
`CSWEET_FIRECRACKER_PARENT_CGROUP` (the process's own cgroup on cgroup v2). Every path must be absolute and
every derived child path is checked for containment.

## Behaviour worth knowing

- **The jailer argument list is built in one testable method.** `BuildJailerArguments` emits
  `--new-pid-ns` (no network namespace, so no jail networking), `--resource-limit no-file=1024`, and cgroup v2
  limits as `memory.max = (MemoryMegabytes + 128 MiB)`, `pids.max = MaximumProcessCount`, and
  `cpu.max = max(1000, CpuPercent × 1000) 100000`. `FirecrackerHelperSecurityTests` asserts that this list
  keeps namespaces and hard resource limits without networking.
- **The probe is a checklist, not a version check.** On Linux it requires a cgroup v2 hierarchy, trusted
  (root-owned, non-writable) firecracker and jailer executables, a trusted kernel and initrd, a protected
  root-only data root, an openable `/dev/kvm`, and identical version strings from firecracker and jailer.
- **Configure is a fixed sequence of API calls** against the jail's socket: `/logger`, `/serial`,
  `/machine-config` (`smt=false`, `track_dirty_pages=false`), `/boot-source` with
  `console=ttyS0 reboot=k panic=1 root=/dev/vda ro systemd.volatile=state`, a read-only `rootfs` drive, a
  writable `scratch` drive, an optional read-only `artifact` ISO drive, and `/vsock` with the derived guest
  CID and `/run/guest.vsock` UDS path. Paths are jail-relative; the host paths never appear in the API calls.
- **The guest CID is derived from the instance id**, and the guest channel is opened by connecting to
  `/run/guest.vsock` in the jail and writing `CONNECT {port}\n` (default 5000), then requiring an `OK <port>`
  acknowledgement. The helper refuses to open a channel unless the recorded process id is still running and
  owned by the jail root.
- **Instance identity is verified, not assumed.** Every lifecycle call loads `instance.json`, requires the
  instance id, workload id, and kind to match the handle, and verifies that the recorded process id belongs to
  this jail (`/proc/<pid>` root check) before acting.
- **Artifact media is path-checked and re-verified.** The ISO must be a direct child of the approved media
  root named `{digest-without-prefix}.iso`, and it is hashed again before staging into the jail.
- **`reap` matches the Hyper-V predicate**: runtime and toolchain-build instances only, with lease expiry,
  finished timestamps, dead processes, and a five-minute creation grace period.
- **File permissions are explicit.** Protected directories are created root-only, and staged files are set to
  root-owned read-only modes before the jail is trusted.
- **The bridge teardown is intentional.** When either copy direction completes, the helper awaits the other
  direction's completion through a continuation that observes its exception rather than letting it surface —
  the guest socket closing during shutdown is normal.

## Related tests

| Test class | What it pins |
|---|---|
| `FirecrackerHelperSecurityTests` | Only the fixed typed operation surface is accepted; jailer arguments enforce namespaces and hard resource limits without networking; protected paths reject traversal; the vsock handshake is bounded and preserves broker bytes. |
| `ExternalPlatformStdioGuestChannelConnectorTests` | The client side of the handshake framing that this helper produces. |
| `HyperVInstanceReapingTests` | The equivalent reap predicate on the Hyper-V side, which the Firecracker predicate mirrors. |

## Related documentation

- [20-security/09-helper-protocol.md](../../20-security/09-helper-protocol.md) — protocol, operations, and
  what helpers cannot do.
- [30-workloads/07-provider-backends.md](../../30-workloads/07-provider-backends.md) — the Linux jail layout
  and lifecycle beside the other backends.
- [10-system/06-process-network-and-storage-surface.md](../../10-system/06-process-network-and-storage-surface.md)
  — the Linux data roots and sockets.
- [runtime-firecracker.md](runtime-firecracker.md) — the library half that starts this executable.

## Sources

`src/CSweet.Office.Runtime.Firecracker.Helper/CSweet.Office.Runtime.Firecracker.Helper.csproj`,
`src/CSweet.Office.Runtime.Firecracker.Helper/{Program,HelperArguments,FirecrackerHelperController,FirecrackerHelperPaths,FirecrackerApiClient}.cs`,
`tests/CSweet.Office.Tests/FirecrackerHelperSecurityTests.cs`.

Verified: 2026-09-15.
