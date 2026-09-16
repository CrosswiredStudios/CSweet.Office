# CSweet.Office.Runtime.HyperV.Helper

The privileged Hyper-V lifecycle helper. It is a console application with no service registration, no
listener, and no long-lived state: RuntimeHost starts it as a child process, passes `--protocol 1.0` and one
operation name, writes a single typed JSON request to its standard input, closes the stream, and reads a
single typed JSON response. Everything the helper does to Hyper-V happens through generated PowerShell.

## Project facts

| Fact | Value |
|---|---|
| Output kind | console exe (`Microsoft.NET.Sdk`, `<OutputType>Exe</OutputType>`) |
| Target framework | `net10.0` (inherited from `Directory.Build.props`) |
| Project references | `Runtime.Core`, `Runtime.HyperV` |
| Key package references | none of its own |
| `AssemblyName` | `CSweet.Office.Runtime.HyperV.Helper` (explicit) |
| `RootNamespace` | not set (defaults to the assembly name) |
| `InternalsVisibleTo` | `CSweet.Office.Tests` |
| csproj `<Description>` | "Privileged, narrow Hyper-V lifecycle helper for C-Sweet RuntimeHost." |

## Key types

| Type | Kind | Responsibility |
|---|---|---|
| `Program` | top-level statements | Argument parse, protocol check, bounded read of one request, controller call, single response write. Always exits 0. |
| `HelperArguments` | record (internal) | Parses `--protocol` / `--operation`; `AllowedOperations` is exactly `probe`, `create`, `start`, `inspect`, `stop`, `destroy`, `reap`, `logs`. |
| `HelperProtocolException` | exception (internal) | Carries the protocol error code returned to the caller. |
| `HyperVHelperController` | class (internal) | The eight operations, plus instance metadata persistence and the reap predicate. |
| `HyperVInstanceMetadata` | record (internal) | Instance id, workload id and kind, VM name, creation/start/finish timestamps, lease expiry. Persisted as `instance.json` inside the instance directory. |
| `HyperVHelperPaths` | record (internal) | `DataRoot`, `InstancesRoot`, `VmConfigurationRoot`, `ArtifactMediaRoot`; `Resolve()` reads `CSWEET_HYPERV_DATA_ROOT` and `CSWEET_ARTIFACT_MEDIA_ROOT`; `InstanceDirectory(id)` enforces containment. |
| `HyperVSocketRegistration` | static class (internal) | `IsConfigured()`: reads `CSWEET_HYPERV_BROKER_SERVICE_ID` and confirms the registry key exists. |
| `PowerShellHyperV` | static class (internal) | The four embedded scripts (`CreateShellScript`, `ConfigureScript`, plus per-command one-liners) and a bounded, encoded-command runner. |
| `HyperVCommandException` | exception (internal) | Error code plus sanitized message (`unsupported-host`, `powershell-unavailable`, `hyperv-timeout`, `not-found`, `hyperv-command-failed`, `powershell-start-failed`). |

## Entry points / composition

`Program.cs` is short and deliberately dumb:

1. Parse arguments and reject anything that is not protocol `1.0` with an allowed operation.
2. Read the request body with a 1 MiB character ceiling.
3. `new HyperVHelperController(HyperVHelperPaths.Resolve())` and call `ExecuteAsync(operation, request)`.
4. Serialize the response to stdout and `return 0` — including for protocol rejections, with a comment
   explaining that a non-zero exit code would make RuntimeHost discard the typed error and replace it with
   stderr.

`ExecuteAsync` switches on the operation name: `probe`, `create`, `start`, `inspect`, `stop`, `destroy`,
`reap`, `logs`; anything else returns `unsupported-operation`.

## Behaviour worth knowing

- **`probe` is the effective-access check.** On Windows it re-runs the host probe, requires a supported
  edition, hardware requirements, an enabled feature with a present hypervisor, and a registered socket key,
  then imports the Hyper-V module and runs `Get-Command New-VM` plus `Get-VMHost`. The comment records why:
  the helper runs as the RuntimeHost service identity, and asking Hyper-V for host state is both stronger and
  more reliable than a local group membership check, which can throw `AccessDenied` for virtual service
  accounts.
- **`create` builds a network-free generation-2 VM.** `CreateShellScript` creates the VM with `-NoVHD`, then
  removes every network adapter and DVD drive. `ConfigureScript` creates a differencing OS disk and a dynamic
  scratch disk, attaches them at SCSI 0/0 and 0/1, optionally attaches the artifact ISO at controller location
  2, sets CPU count and maximum percentage, disables dynamic memory and checkpointing, sets secure boot with
  the Microsoft UEFI CA template, and then asserts the resulting topology: zero network adapters, exactly two
  hard disks, and the expected DVD count. Any mismatch throws, removes the VM, and deletes both disks.
- **CPU percent is converted per virtual processor.** Hyper-V applies `Maximum` uniformly to each vCPU, so the
  helper computes `ceil(cpuPercent / vCpuCount)` clamped to 1-100 before calling `Set-VMProcessor`.
- **Artifact media is copied, not referenced.** For runtime and toolchain workloads the helper copies the
  verified ISO from the artifact media root into the instance directory and re-verifies it there. The source
  path must stay inside the approved media root, must be a direct child of it, and must be named
  `{digest-without-prefix}.iso`.
- **`reap` is metadata-driven and best-effort.** It walks `instances/`, ignores names that are not 32 hex
  characters, skips builder instances and any VM whose name does not start with `CSweet-`, and removes
  instances where `HyperVInstanceMetadata` says the lease expired, the workload finished, the VM is gone, or
  the VM is off and either already started or past the five-minute creation grace period. Failures are logged
  into a comment and retried on a later pass.
- **`logs` returns an empty list.** The helper has no log channel of its own; agents run inside the guest and
  hypervisor session logs are not exposed through this protocol.
- **PowerShell is invoked defensively.** Scripts are passed as Base64 `-EncodedCommand` with `-NoLogo
  -NoProfile -NonInteractive`, environment values are passed per call, output is bounded to 64 000 characters,
  exit code 44 maps to `not-found`, and error text is decoded from CLIXML before being bounded and stripped of
  control characters.
- **Nothing about the helper is configurable by the caller.** The operation set is fixed
  ([SEC-INV-20](../../20-security/11-security-invariants.md)); there is no operation that takes a script, a
  path prefix, a device name, or a mount option from the control plane.

## Related tests

| Test class | What it pins |
|---|---|
| `WindowsHyperVOnboardingTests` | Helper argument parsing: unknown operations rejected, only the typed lifecycle operations accepted, and the reaping operation accepted. Also covers `PowerShellHyperV.Sanitize` behavior that the helper relies on for error text. |
| `HyperVInstanceReapingTests` | `HyperVHelperController.ShouldReap` across the lease, finished, missing, and powered-off cases. |
| `RuntimeHostRpcIntegrationTests` | End-to-end dispatch through a backend shaped like this one, including sanitized provider diagnostics. |

## Related documentation

- [20-security/09-helper-protocol.md](../../20-security/09-helper-protocol.md) — the protocol this executable
  implements.
- [30-workloads/07-provider-backends.md](../../30-workloads/07-provider-backends.md) — the Hyper-V create and
  reap mechanics beside the other two backends.
- [20-security/06-host-privilege-model.md](../../20-security/06-host-privilege-model.md) — why the helper is a
  separate process.
- [runtime-hyperv.md](runtime-hyperv.md) — the library half that starts this executable.

## Sources

`src/CSweet.Office.Runtime.HyperV.Helper/CSweet.Office.Runtime.HyperV.Helper.csproj`,
`src/CSweet.Office.Runtime.HyperV.Helper/{Program,HelperArguments,HyperVHelperController,HyperVHelperPaths,HyperVSocketRegistration,PowerShellHyperV}.cs`,
`tests/CSweet.Office.Tests/{WindowsHyperVOnboardingTests,HyperVInstanceReapingTests}.cs`.

Verified: 2026-09-15.
