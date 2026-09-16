# CSweet.Office.Runtime.HyperV.Helper

The privileged Hyper-V lifecycle helper: a short-lived console process with no service registration, no listener, and no long-lived state. RuntimeHost starts it with `--protocol 1.0 --operation <op>`, writes one typed JSON request to standard input, closes the stream, and reads one typed JSON response. It is a privileged boundary; the operation surface is deliberately narrow.

Part of the C-Sweet Office execution plane. Full documentation: [`docs/80-reference/projects/runtime-hyperv-helper.md`](../../docs/80-reference/projects/runtime-hyperv-helper.md).

**Output:** console exe
**References:** `CSweet.Office.Runtime.Core`, `CSweet.Office.Runtime.HyperV`

## What it owns

- `HyperVHelperController` — the eight operations (`probe`, `create`, `start`, `inspect`, `stop`, `destroy`, `reap`, `logs`), instance metadata persistence, and the reap predicate.
- `HyperVHelperPaths` — resolves the data root, instances root, VM configuration root, and artifact media root from the environment, with containment checks on every derived child.
- `PowerShellHyperV` — the embedded PowerShell scripts and the bounded, encoded-command runner that decodes CLIXML error text.
- `HyperVSocketRegistration` — confirms the vSock registry key for `CSWEET_HYPERV_BROKER_SERVICE_ID` exists.
- `HelperArguments` — accepts only `--protocol` and `--operation` with the fixed operation set.

## Notes for contributors

- The process always exits `0`, including for protocol rejections: `Program.cs` records that a non-zero exit would discard the typed error and replace it with stderr. Do not "fix" the exit code.
- The operation set is fixed (SEC-INV-20); never add a shell, an arbitrary path, or a request-supplied mount option.
- `logs` returns an empty array deliberately; the interface is intentional.
- `create` builds a network-free generation-2 VM and then asserts the topology (zero network adapters, exactly two disks, expected DVD count), removing the VM on any mismatch. Keep the assertion.
- CPU percent is converted per virtual processor before `Set-VMProcessor`, because Hyper-V applies `Maximum` to each vCPU uniformly.

## Tests

`WindowsHyperVOnboardingTests` (helper argument parsing and the PowerShell diagnostic decoding it relies on), `HyperVInstanceReapingTests` (the reap predicate), and `RuntimeHostRpcIntegrationTests` (end-to-end dispatch with sanitized diagnostics).

> **Documentation for this project lives in [`docs/`](../../docs/README.md), not here.** Adding files under this directory is fine, but the guest-image fingerprint roots listed in [docs/50-development/09-guest-image-changes.md](../../docs/50-development/09-guest-image-changes.md) must stay untouched.
