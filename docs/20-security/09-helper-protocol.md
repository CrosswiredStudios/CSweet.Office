# The platform helper protocol

**Audience:** security reviewers; contributors to any helper or `ExternalPlatformIsolationBackend`.

The RuntimeHost runs as `root` or a Hyper-V administrator — too much privilege to expose to request parsing.
Each platform therefore delegates VM lifecycle work to a **helper**: a separate, digest-pinned executable
with a fixed command surface and no ability to be told what to do beyond eight typed operations.

## The RPC shape

Not gRPC. Not HTTP. Typed JSON on the helper's standard streams.

```
<helper> --protocol 1.0 --operation <probe|create|start|inspect|stop|destroy|reap|logs>
```

The request JSON is written to stdin, then stdin is closed. The response JSON is read from stdout.

`PlatformHelperRequest` carries: `WorkloadKind` (Builder / Runtime / ToolchainBuild), `Handle`,
`GuestImagePath`, `ArtifactImagePath`, `GracePeriodSeconds`, `MaximumBytes`.

`PlatformHelperResponse` carries: `Success`, `ErrorCode`, `SanitizedError`, `ProviderInstanceId`, `Status`,
`Logs`, `WorkloadsRemoved`, `GuestChannelTransport`.

> The class documentation states the rule directly: *"no shell, raw command, host path, network-device, or
> mount option is accepted from the control plane."*

## Every invocation re-verifies the helper binary

`ExternalPlatformIsolationBackend.InvokeAsync` performs, on **every** call:

1. Helper path is fully qualified and exists.
2. `VerifyFileDigestAsync` against `HelperExecutableDigest` (canonical lowercase `sha256:`), compared with
   `FixedTimeEquals`. Failure throws `IOException` — *"failed its integrity check"*.

There is no cache and no "verify once at startup" shortcut. A helper binary replaced mid-life is detected on
the next operation.

### Process launch

| Aspect | Value |
|---|---|
| `UseShellExecute` | `false` |
| stdin / stdout / stderr | redirected |
| `CreateNoWindow` | `true` |
| Argument list | **solely** `--protocol 1.0 --operation <op>` |
| stdout bound | 1 MiB — over-limit throws `InvalidDataException` |
| stderr bound | 16 KiB — over-limit throws `InvalidDataException` |
| Timeout | `Math.Clamp(HelperTimeoutSeconds, 5, 600)`, default 120 s; on expiry `process.Kill(entireProcessTree: true)` |
| Non-zero exit | `IOException` with a sanitized stderr (`Sanitize` strips control characters, 256 characters) |

### Helpers exit 0 even on typed failure

A typed failure is a normal response with `Success = false` and an `ErrorCode`. The Hyper-V helper's
`Program.cs` documents why: a non-zero exit would make RuntimeHost discard the typed error and replace it with
a generic `IOException`. Changing the helper to exit non-zero on failure would destroy error reporting
without improving safety.

## The operation allow-list

Eight operations, enforced by `HelperArguments.Parse` in each helper. Anything else throws
`HelperProtocolException("invalid-arguments")`. Test
`HelperArguments_RejectUnknownOperations` pins this.

| Operation | Purpose |
|---|---|
| `probe` | Host readiness, including guest image digests, signature file, pinned signing certificate (thumbprint plus validity window, RSA-PKCS1 or ECDSA), the certification evidence file digest **and** every identity field inside it, and an active certification window. Also requires `GuestChannelTransport == RequiredGuestChannelTransport`. |
| `create` | Create the VM or jail (or, on macOS, the workload-host process). |
| `start` | Boot it. |
| `inspect` | Status, termination reason, exit code, timestamps. |
| `stop` | Graceful stop with a bounded grace period. |
| `destroy` | Remove the VM, disks, and instance directory. |
| `reap` | Remove abandoned instances. |
| `logs` | Bounded log read. |

## What a helper cannot do

### It cannot accept an arbitrary operation or argument

Only `--protocol` and `--operation` are parsed, and only against the allow-list above. No request-supplied
string is ever passed to a command interpreter.

### It cannot choose host paths

- The VM name is helper-generated: `CSweet-{Kind}-{newGuid:N}`.
- Instance directories derive from the Hyper-V VM GUID.
- The guest image must be an existing absolute VHDX.
- Artifact media must resolve beneath `HyperVHelperPaths.ArtifactMediaRoot` and pass digest verification.
- `InstanceDirectory(Guid)` re-checks the resolved path prefix and throws `invalid-instance` on escape.

### It cannot run arbitrary PowerShell

`PowerShellHyperV` executes **five fixed script constants** via
`powershell.exe -NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand <base64(script)>`.
Data reaches a script only through environment variables (`CSWEET_VM_NAME`, `CSWEET_BASE_DISK`,
`CSWEET_MEMORY_BYTES`, and so on). There is no string interpolation of request data into script text.

PowerShell's own CLI-XML error output is decoded and sanitized by `PowerShellHyperV.Sanitize`, pinned by
`WindowsHyperVOnboardingTests`.

### It cannot create an unsafe VM

The create script enforces, inside PowerShell:

| Invariant | Mechanism |
|---|---|
| No network device | Throws if any adapter exists on the new VM. |
| Exactly two disks | Validated count. |
| Expected DVD count | Validated count. |
| Secure Boot on, Microsoft UEFI CA template | `Set-VMFirmware -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority` |
| No checkpoints | `-CheckpointType Disabled` |
| No automatic start | `AutomaticStartAction Nothing` |
| Cleanup on failure | Removes the VM and both disks |

### It cannot act on handles it never created

State is on disk — an `instance.json` per instance directory — and lookups go by GUID from the request
handle. A handle that does not correspond to a live instance fails.

### It cannot attach artifact media to a builder workload

*"Builder workloads cannot attach runtime artifact media."* Enforced in the backend before invocation.

### It cannot serve logs on Windows

`Logs()` returns an empty array. The interface exists; the Hyper-V implementation is a stub. The practical
consequence: on Windows the Office's `LogExcerpt` is usually empty, and builder output arrives through the
guest's `runtime.logs` stream instead. See
[12-known-limits-and-tradeoffs.md](12-known-limits-and-tradeoffs.md).

## Per-platform specifics

### Hyper-V (`CSweet.Office.Runtime.HyperV.Helper`)

Helper-managed: VM creation, disks, DVD, processor, firmware, start, stop, destroy, reap. **Not**
helper-managed: the guest channel. `WindowsHyperVSocketTransport` lives in the RuntimeHost process and opens
the `AF_HYPERV` socket itself.

### Firecracker (`CSweet.Office.Runtime.Firecracker.Helper`)

`FirecrackerApiClient` talks to the Firecracker API over a **raw Unix domain socket** using
`SocketsHttpHandler.ConnectCallback` (`Socket(AF_UNIX, Stream)` → `NetworkStream`) with
`BaseAddress = http://localhost`, using only PUT and GET against `/`, `/version`, and its own endpoints.

The helper also owns the guest channel: it connects to `<jail>/run/guest.vsock`, sends `CONNECT <port>\n`,
requires `OK <port>`, writes exactly one newline-terminated JSON handshake line, then bridges stdin/stdout to
the guest. It requires the metadata process to still be alive and owned by the jail root.

### Apple Virtualization (`CSweet.Office.Runtime.AppleVirtualization.Helper`)

A Swift executable. `create` spawns a separate workload-host process
(`<helper> --workload-host <instance.json>`) that owns the `VZVirtualMachine` and listens on
`/var/run/csweet-av/<32-hex>.sock`. Other operations send newline-terminated JSON carrying a manager token.

`openGuestChannel` asks the manager to connect the `VZVirtioSocketDevice` and passes the connected socket
descriptor back — descriptor passing rather than data tunnelling.

## Why this design

Three properties, each independently useful:

1. **Blast radius.** A parsing bug in a request handler does not give an attacker the ability to run a
   command; there is no command to run.
2. **Detectability.** Re-verifying the helper digest per invocation means binary tampering has a narrow
   window and is detectable without a separate integrity monitor.
3. **Auditability.** Eight operations with typed JSON fit on one page, so the privileged surface can be
   reviewed exhaustively.

Related invariants: `SEC-INV-20`.

## Sources

`src/CSweet.Office.Runtime.Core/{ExternalPlatformIsolationBackend.cs,PlatformHelperContracts.cs}`,
`src/CSweet.Office.Runtime.HyperV.Helper/{Program.cs,HelperArguments.cs,PowerShellHyperV.cs,HyperVHelperPaths.cs,HyperVHelperController.cs}`,
`src/CSweet.Office.Runtime.Firecracker.Helper/{FirecrackerApiClient.cs,FirecrackerHelperPaths.cs,HelperArguments.cs}`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Sources/CSweetAppleVirtualizationHelper/*.swift`,
`tests/CSweet.Office.Tests/{FirecrackerHelperSecurityTests.cs,PlatformIsolationBackendTests.cs,WindowsHyperVOnboardingTests.cs}`.

Verified: 2026-09-15.
