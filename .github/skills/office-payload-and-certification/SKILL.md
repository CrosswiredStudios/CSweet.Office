---
name: office-payload-and-certification
description: "Use when building, rebuilding, certifying, signing, or installing a C-Sweet Office runtime payload on any platform — the Windows Hyper-V loop, the Linux Firecracker loop, or the macOS loop — including guest image rebuild decisions, the guest build fingerprint, stale-payload traps, and certification evidence requirements."
---

# Office payload and certification

This skill covers the loop between "I changed code" and "the machine is running the new code", for all three
platforms. It exists because that loop has several traps that silently produce a working-looking install of the
wrong bits.

## Decide what must be rebuilt

| You changed | Guest image | Payload | Re-certify |
|---|---|---|---|
| `RuntimeGuest`, `BuilderGuest`, `ToolchainGuest` | **yes** | yes | yes |
| Anything under `src/CSweet.Office.Runtime.Protocol` | **yes** | yes | yes |
| `build/windows-hyperv/**` | **yes** | yes | yes |
| `RuntimeHost`, `Node`, `Configurator`, `Runtime.HyperV.Helper` | no | yes | no |
| `Runtime.Firecracker.Helper` | no | yes | no |
| A provider capability, descriptor, or the broker protocol version | no | yes | **yes** |
| Installer scripts only | no | yes (they are packaged) | no |

The guest build fingerprint hashes every non-`bin`/`obj` file under
`src/CSweet.Office.RuntimeGuest`, `src/CSweet.Office.BuilderGuest`, `src/CSweet.Office.ToolchainGuest`,
`src/CSweet.Office.Runtime.Protocol`, `build/windows-hyperv`, the sibling
`CSweet.Isolation/tools/LinuxImage`, and `scripts/windows/New-CSweetHyperVTestGuest.ps1` — plus the root
`Directory.Build.props`, `Directory.Packages.props`, and `global.json` when present.

**Adding any file under those paths, including a README, changes the fingerprint and forces a full Packer guest
rebuild.** Put documentation in `docs/`.

## Windows loop

Run `.\scripts\windows\Initialize-CSweetWindowsIsolationTest.ps1`. It needs an elevated session, the sibling
`CSweet.Isolation` repository, the Hyper-V feature, `vmms`, and an existing virtual switch. It will exit `0`
with a `restart-required` progress record if a reboot is needed — reboot and re-run.

It publishes three binaries (helper, probe, smoke), runs the certification smoke test against a real guest,
asserts every evidence check is `true`, signs the guest image with a development signer
(`CN=C-Sweet Windows Hyper-V Development Guest Image Signer`, RSA 3072, SHA-256, 1 year), assembles the payload, and
installs unless `-SkipInstall` is passed.

Then select the **newest complete** payload and verify it is not stale before installing. Full procedure with
commands is in
[`../../../docs/50-development/04-windows-dev-loop.md`](../../../docs/50-development/04-windows-dev-loop.md).

## Linux loop

Run `sudo ./scripts/linux/initialize-firecracker-test.sh --control-plane <url>`. It needs root, Ubuntu 24.04,
cgroup v2, and a readable+writable `/dev/kvm`. It downloads Firecracker with a checksum check, builds the guest
ext4 image, publishes the helper and smoke test, runs the smoke test, asserts every evidence check is `true`,
creates an OpenSSL development signer, and assembles the payload.

See [`../../../docs/50-development/05-linux-dev-loop.md`](../../../docs/50-development/05-linux-dev-loop.md).

## macOS loop

There is no automated isolation test script in this repository. Build the Swift helper from
`src/CSweet.Office.Runtime.AppleVirtualization.Helper` (it needs the `com.apple.security.virtualization`
entitlement) and assemble the payload with `scripts/macos/new-runtime-payload.sh`. See
[`../../../docs/50-development/06-macos-dev-loop.md`](../../../docs/50-development/06-macos-dev-loop.md).

## Traps

1. **Stale payload.** The RuntimeHost installer skips files whose installed SHA-256 already matches the
   manifest, so re-running it against an old payload is a silent no-op. Always rebuild, then select by
   `LastWriteTime -Descending` and check the embedded Office version.
2. **Never select an older `certification-*` directory** after a rebuild.
3. **Two version thresholds.** The installer gate is `< 0.1.0.0`; its message names a different version. Trust
   the threshold.
4. **Certification expiry is a hard stop.** An Office whose certification window has lapsed stops accepting
   work rather than degrading (`SEC-INV-10`).
5. **Windows provider logs do not exist.** The Hyper-V helper's `Logs()` is a stub; use the guest
   `runtime.logs` stream and the Event Log.
6. **A helper change changes the certified configuration.** Re-verify `HelperExecutableDigest`, which is
   computed from disk, never trusted from the manifest.
7. **Upgrades require drain.** An office must be draining with zero active assignments before an
   identity-preserving upgrade (`SEC-INV-18`).

## Where the detail lives

- [`../../../docs/30-workloads/06-guest-images.md`](../../../docs/30-workloads/06-guest-images.md) — images, runners, device layout, fingerprint.
- [`../../../docs/60-release/02-payload-and-manifest.md`](../../../docs/60-release/02-payload-and-manifest.md) — payload layout and the manifest schema.
- [`../../../docs/60-release/03-certification.md`](../../../docs/60-release/03-certification.md) — evidence shape and signer certificates.
- [`../../../docs/40-operations/07-diagnostics-and-troubleshooting.md`](../../../docs/40-operations/07-diagnostics-and-troubleshooting.md) — when the install succeeds but nothing runs.
