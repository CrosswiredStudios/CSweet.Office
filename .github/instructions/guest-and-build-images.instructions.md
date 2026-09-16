---
description: "Rules for in-guest code and guest image build inputs: RuntimeGuest, BuilderGuest, ToolchainGuest, GuestProbe, and build/. Covers the boot contract, the environment allow-list, setpriv, mount flags, and the guest-image fingerprint that makes any new file expensive."
applyTo: "src/CSweet.Office.RuntimeGuest/**,src/CSweet.Office.BuilderGuest/**,src/CSweet.Office.ToolchainGuest/**,src/CSweet.Office.GuestProbe/**,build/**"
---

# In-guest code and guest image inputs

**Stop before you add a file here.** `Get-GuestBuildFingerprint` in
`scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1` SHA-256 hashes every non-`bin`/`obj` file under
`src/CSweet.Office.RuntimeGuest`, `src/CSweet.Office.BuilderGuest`, `src/CSweet.Office.ToolchainGuest`,
`src/CSweet.Office.Runtime.Protocol`, and `build/windows-hyperv` (plus the sibling
`CSweet.Isolation/tools/LinuxImage` and `New-CSweetHyperVTestGuest.ps1`). A new file there invalidates the
cached guest image and triggers a full Packer rebuild.

Documentation for these projects belongs in [`docs/`](../../docs/30-workloads/06-guest-images.md), not beside
the code. See [`docs/50-development/09-guest-image-changes.md`](../../docs/50-development/09-guest-image-changes.md).

## Never change these

| Rule | Invariant |
|---|---|
| The workload environment is cleared and rebuilt from the 17-key allow-list, plus fixed injections. Never allow `PATH` or a loader-affecting variable. | `SEC-INV-21` |
| Keep the forced `CSweet__Agent__ManifestPath = csweet-plugin.json` override. | `SEC-INV-21` |
| Keep the two-device artifact allow-list, the `RDONLY \| NOSUID \| NODEV \| NOEXEC` mount flags, the whole-stream digest check, and the extraction limits. | `SEC-INV-22` |
| Keep the boot-token HMAC proof, the one-shot challenge, and lease-expiry cancellation. | `SEC-INV-23` |
| Workload executables stay inside the artifact root. The single exception is the certified toolchain runner for kind 2. | `SEC-INV-21` |

## The three kinds

| Kind | Runner | Artifact media | Identity env |
|---|---|---|---|
| `0` Builder | `CSweet.Office.BuilderGuest` from `/usr/lib/csweet/builder/` | none | not required |
| `1` Runtime | the materialized `payload/<entrypoint>` | required | required |
| `2` ToolchainBuild | `/usr/lib/csweet/toolchain/CSweet.Office.ToolchainGuest` | required | required |

Kind 0 is the only kind that skips the artifact and identity validation and may use an arbitrary absolute
artifact root. If you change kind semantics, `GuestServiceOptions.Validate` and
[`docs/30-workloads/03-builder-and-toolchain-lifecycles.md`](../../docs/30-workloads/03-builder-and-toolchain-lifecycles.md)
must change together.

## Build scripts

`build/linux-firecracker/provision-guest.sh` and `build/windows-hyperv/provision-guest.sh` define the boot
contract: `prepare-runtime.sh` validates `/dev/vdb` (writable) and `/dev/vdc` (must report read-only, exit 5
otherwise), formats and mounts scratch at `/run/csweet`, then `exec`s the broker.

- The read-only check on `/dev/vdc` is a security control. Keep it.
- Keep `NoNewPrivileges=false` on the guest unit. The workload's `--no-new-privs` comes from `setpriv`; the
  service itself must be able to drop privileges.
- Never add a network device. Every provider creates guests with no NIC and Hyper-V throws if one exists.

## Bundling rule

`BuilderGuest` and `ToolchainGuest` archive their publish output directory verbatim. Never add
`CopyToOutputDirectory` to a non-code file in a project that is published into a guest image, or it will end up
inside an artifact bundle.

## Tests that will catch you

`GuestArtifactMaterializerTests.cs`, `WorkspaceEnvironmentTests.cs`, `GuestLocalBrokerProxyTests.cs`,
`ToolchainGuestSecurityTests.cs`.
