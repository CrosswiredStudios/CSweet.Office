# Changing the guest image

**Audience:** anyone editing a guest project, the packer profile, or the guest provisioning scripts.

The Windows development loop caches the Hyper-V guest image behind a build fingerprint. Everything in the
list below is that fingerprint, and everything in it invalidates the cache when it changes — including
files that are not build inputs. This page exists so that cost is a decision, not a surprise.

> **Scope:** the fingerprint is a Windows-only mechanism. The Linux loop rebuilds the guest on every run
> (`scripts/linux/new-firecracker-guest.sh` is called unconditionally), and the macOS payload consumes a
> guest image this repository does not build. See [05](05-linux-dev-loop.md) and [06](06-macos-dev-loop.md).

## What lives in the guest image

| Guest | Contents | Built by |
|---|---|---|
| Hyper-V (`csweet-agent-guest-*.vhdx`) | Ubuntu guest with the three runners installed under `/usr/lib/csweet/guest/`, the `csweet-agent-guest.service` unit, and `prepare-runtime.sh` as the service entry point. | The sibling `CSweet.Isolation` module `tools/LinuxImage` through the profile in `build/windows-hyperv`, driven by `New-CSweetHyperVTestGuest.ps1`. |
| Firecracker (`csweet-agent-guest.ext4`) | Minimal Ubuntu `noble` root filesystem (`systemd-sysv`, `udev`, `e2fsprogs`, `util-linux`, `kmod`, `ca-certificates`, `libicu74`, `libssl3t64`, `zlib1g`, `linux-image-virtual`), the three runners plus the certification probe, the copied host .NET root at `/usr/share/dotnet`, and the guest service provisioned by `build/linux-firecracker/provision-guest.sh`. | `scripts/linux/new-firecracker-guest.sh` on the development host. |
| Kernel and initrd | Extracted from each root filesystem (`vmlinux`/`vmlinuz`, `initrd.img`) and shipped in the payload beside the image. | Same scripts as the image. |

The in-image layout is asserted by tests, not assumed: `WindowsHyperVOnboardingTests.GuestService_KeepsScratchMountInBrokerProcess`
requires the provisioning script to contain `exec /usr/lib/csweet/guest/CSweet.Office.RuntimeGuest` and
`ExecStart=/usr/lib/csweet/prepare-runtime.sh`, and forbids `ExecStartPre=`.

## The three runners

| Runner | Workload kind | Role |
|---|---|---|
| `CSweet.Office.RuntimeGuest` | 1 (runtime) | In-guest broker and workload supervisor; owns the scratch device mount and the guest handshake. |
| `CSweet.Office.BuilderGuest` | 0 (builder) | In-guest plugin build runner. |
| `CSweet.Office.ToolchainGuest` | 2 (toolchain build) | In-guest toolchain adapter runner. |

`CSweet.Office.GuestProbe` is not a workload runner. It is the certification probe the smoke test starts
inside a disposable guest, and it is published into the Linux guest tree for that purpose.

## Publish steps

Every guest executable is published self-contained, single file, for the guest RID:

```bash
dotnet publish src/CSweet.Office.RuntimeGuest/CSweet.Office.RuntimeGuest.csproj \
    -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o <publish>
```

- Windows path (`New-CSweetHyperVTestGuest.ps1`): publishes `RuntimeGuest`, `BuilderGuest`, and
  `ToolchainGuest` and copies **only the single-file executable** into the image staging payload as
  `<Project>.bin`.
- Linux path (`new-firecracker-guest.sh`): publishes the three runners plus `GuestProbe` and installs each
  with mode `0755` into the root filesystem before `provision-guest.sh` runs in a chroot.

Publishing by hand is not enough. The certification step is what produces the evidence that payload
assembly requires, and it only exists as part of the loops in [04](04-windows-dev-loop.md) and
[05](05-linux-dev-loop.md).

## The fingerprint function

`Get-GuestBuildFingerprint` in `scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1` enumerates
these entries. Only files are hashed, recursively, and any path containing a `bin` or `obj` segment is
skipped.

| Class | Entry | Touching it |
|---|---|---|
| In-repo root | `src\CSweet.Office.RuntimeGuest` | Any file, including one the build ignores. |
| In-repo root | `src\CSweet.Office.BuilderGuest` | Any file. |
| In-repo root | `src\CSweet.Office.ToolchainGuest` | Any file. |
| In-repo root | `src\CSweet.Office.Runtime.Protocol` | Any file; this is the guest envelope contract. |
| In-repo root | `build\windows-hyperv` | Any file; this is the Packer profile. |
| Sibling root | `..\CSweet.Isolation\tools\LinuxImage` | Any file in the shared image module. A missing checkout fails the fingerprint outright. |
| Script | `scripts\windows\New-CSweetHyperVTestGuest.ps1` | The guest builder itself. |

In addition, these single files are hashed when they exist:

| File | Note |
|---|---|
| `Directory.Build.props` | Present. |
| `Directory.Packages.props` | Present; a package version bump that a guest project uses rebuilds the image. |
| `global.json` | Absent today; it would be hashed if it appeared. |

Record format: for each file, one line of `<path relative to the parent directory of the repository root>`
and its lowercase SHA-256, with the records sorted by full path; the fingerprint is the SHA-256 of the
joined records. The relative form is why the sibling checkout's files appear as
`CSweet.Isolation/tools/LinuxImage/...`.

Cache behaviour:

- The marker is `<image>.vhdx.ready`, containing the fingerprint verbatim.
- Reuse scans `artifacts\windows-runtime\source` for `csweet-agent-guest-<fingerprint>*.vhdx.ready`,
  newest first, up to 32 candidates, and takes the first whose image exists and whose marker matches
  exactly.
- `-RebuildGuest` forces a build; the new image gets a GUID suffix, and older images are left in place.

> **The rule.** Adding **any** file under an in-repo root — a test asset, a scratch file, or a
> documentation page such as `src/CSweet.Office.RuntimeGuest/README.md` — changes the fingerprint and
> forces a full guest rebuild. Put documentation for those four projects somewhere else in the tree,
> and never let an editor drop a temporary file into them.

## Consequences of touching each entry

| You changed | Guest image rebuild? | Payload rebuild? | Re-certify? |
|---|---|---|---|
| `RuntimeGuest`, `BuilderGuest`, or `ToolchainGuest` source | Yes | Yes | Yes |
| `Runtime.Protocol` (guest envelope types) | Yes | Yes | Yes |
| `build\windows-hyperv` (Packer profile, provisioning) | Yes | Yes | Yes |
| `New-CSweetHyperVTestGuest.ps1` | Yes | Yes | Yes |
| `CSweet.Isolation\tools\LinuxImage` (sibling) | Yes | Yes | Yes |
| `Directory.Build.props`, `Directory.Packages.props`, `global.json` | Yes | Yes | Yes |
| Any file added under the roots above, including documentation | Yes | Yes | Yes |
| Host code (`Node`, `RuntimeHost`, `Runtime.LocalRpc`, providers, helpers) | No — cached image is reused | Yes | No, the previous evidence still describes the same image |
| Test project only | No | No | No |

"No" in the re-certify column means the existing evidence remains valid; it does not mean no evidence is
produced. Every `Initialize-CSweetWindowsIsolationTest.ps1` run executes the certification smoke test and
writes fresh evidence, even when the guest image comes from cache.

## Rebuild cost

- The script's own progress estimates put guest preparation at 900–2700 seconds (15–45 minutes) and the
  in-VM Packer build phase at 300–2400 seconds (5–40 minutes), with a heartbeat every 30 seconds.
- Certification adds 180–900 seconds (3–15 minutes); publishing the three binaries adds 180–600 seconds.
- The Linux guest build has no cache at all, so a guest source change costs a full `debootstrap` plus
  chroot provisioning on every run.

## Certification afterwards

Provider selection requires an active certification whose `GuestImageDigest` matches the image in play,
and a provider is never returned without one (`SEC-INV-10`). Evidence is bound to the provider identity,
the guest image digest, and the broker protocol version. Therefore:

- A guest image change invalidates every previous evidence file for that image; re-run the platform loop
  and let it produce new evidence before building or installing a payload.
- Payload assembly consumes the evidence file and stores its digest in `runtime-manifest.json`; the
  provider probe re-verifies both the image and the evidence digest at startup.
- The certification window matters as much as the digest: `certificationExpiresAt` from the evidence is
  carried into the payload and enforced by `IsolationProviderProbeResult`-based selection.

## Sources

`scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1`, `scripts/windows/New-CSweetHyperVTestGuest.ps1`,
`scripts/windows/New-CSweetWindowsRuntimePayload.ps1`, `scripts/linux/new-firecracker-guest.sh`,
`scripts/linux/new-runtime-payload.sh`, `build/windows-hyperv` (profile directory),
`build/linux-firecracker/provision-guest.sh`, `src/CSweet.Office.Runtime.Core/{ExternalPlatformIsolationBackend.cs,PlatformRuntimePayloadManifest.cs}`,
`tests/CSweet.Office.Tests/WindowsHyperVOnboardingTests.cs`, `docs/20-security/11-security-invariants.md`,
`docs/30-workloads/06-guest-images.md`.

Verified: 2026-09-19.
