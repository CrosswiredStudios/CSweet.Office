# Windows development loop

**Audience:** a contributor on Windows with, or willing to enable, Hyper-V. This is the loop that turns
host and guest source into an installed, certified RuntimeHost.

```powershell
.\scripts\windows\Initialize-CSweetWindowsIsolationTest.ps1
```

That single command performs host preflight, guest image preparation, component publishing, a real
no-network certification run, guest image signing, payload assembly, and installation. Everything below
describes what it does, what it caches, and where it silently does nothing.

## The loop, in order

| # | Step | Evidence on disk |
|---|---|---|
| 1 | Host preflight: 64-bit Windows, `Microsoft-Hyper-V` optional feature, `vmms` service, `Get-VMHost`, switch lookup | progress file only |
| 2 | Guest build fingerprint and cache check; build the guest image when needed | `artifacts\windows-runtime\source\csweet-agent-guest-<fingerprint>*.vhdx` + `.ready` marker |
| 3 | Publish the Hyper-V helper, the Linux probe, and the smoke runner | `artifacts\windows-test\certification-<yyyyMMdd-HHmmss>\{helper,probe,smoke}` |
| 4 | Run the certification smoke test against real no-network VMs | `<runRoot>\windows-hyperv.json`, `<runRoot>\certification.log` |
| 5 | Sign the certified guest image with the development signer certificate | `<runRoot>\guest-image-signing.cer`, `<guestImage>.sig` |
| 6 | Assemble the payload | `<runRoot>\payload\runtime-manifest.json` |
| 7 | Install RuntimeHost and Office, unless `-SkipInstall` | `%ProgramFiles%\CSweet\Office` |

## Parameters

| Parameter | Default | Effect |
|---|---|---|
| `-SwitchName` | `Default Switch` | Hyper-V switch used to build the guest image; a missing switch fails the run and names the available switches. |
| `-RebuildGuest` | off | Forces a guest image build even when the fingerprint cache is valid. |
| `-SkipInstall` | off | Stops after payload assembly. |
| `-NoElevation` | off | Refuses to re-launch; without administrator rights the script throws instead of elevating. |
| `-ControlPlaneUserSid` | current user SID | Identity permitted to call the RuntimeHost pipe and to own Node state. |
| `-ControlPlaneUrl` | — | Passed through to the installer. |
| `-EnrollmentTokenInputPath` | — | Passed through to the installer; the enrollment token is never a command-line argument. |
| `-ProgressPath` | `%ProgramData%\CSweet\Setup\windows-isolation-<jobId>.json` | Where progress is journaled. |
| `-ProgressJobId` | new GUID | Correlates progress writes for the C-Sweet guided-setup surface. |

## Self-elevation

Without administrator rights the script re-launches itself:

- It starts `System32\WindowsPowerShell\v1.0\powershell.exe` with `-NoLogo -NoProfile -ExecutionPolicy Bypass -File <script>`
  plus the original arguments and `-NoElevation`, using `Start-Process -Verb RunAs -Wait -PassThru`.
- Windows shows the administrator prompt. If the elevated process exits non-zero, the parent throws.
- With `-NoElevation` and no administrator rights, it throws `Administrator access is required to prepare and test Hyper-V.`

## Restart-required behaviour

Hyper-V enablement is not fully applied until Windows restarts. The script handles that as a first-class
success state, not a failure:

- If the feature is already `EnablePending`, it writes progress with state `restart-required` and returns.
- If enabling the feature reports `RestartNeeded`, it writes the same state and calls `exit 0`.

Exit code `0` therefore means "re-run me after the restart" in this one case. The progress file carries
`requiresRestart` so the calling surface can say the same thing.

## Progress file

The default path is `%ProgramData%\CSweet\Setup\windows-isolation-<jobId>.json`, written through
`CSweet.WindowsSetupProgress.ps1` with the workflow key `developer-bootstrap`. Phase keys, in order:
`host-preflight`, `enable-hyperv`, `windows-restart`, `prepare-guest`, `guest-cache`,
`publish-components`, `certify-runtime`, `sign-guest`, `package-runtime`, and finally
`package-complete` or `setup-complete`. While the certification run is in flight a background job
heartbeats `certify-runtime` every 15 seconds so a long smoke run does not look hung. Failures are
written with the last known phase, a percentage, and `developer-bootstrap-failed`.

## Guest image cache and the fingerprint rule

`Get-GuestBuildFingerprint` hashes every non-`bin`/`obj` file under these entries, recursively:

| Entry | Kind |
|---|---|
| `src\CSweet.Office.RuntimeGuest` | in-repo directory |
| `src\CSweet.Office.BuilderGuest` | in-repo directory |
| `src\CSweet.Office.ToolchainGuest` | in-repo directory |
| `src\CSweet.Office.Runtime.Protocol` | in-repo directory |
| `build\windows-hyperv` | in-repo directory |
| `scripts\windows\New-CSweetHyperVTestGuest.ps1` | in-repo file |
| `..\CSweet.Isolation\tools\LinuxImage` | sibling-repository directory |

and additionally hashes these files when they exist:

| File | Note |
|---|---|
| `Directory.Build.props` | added only when present |
| `Directory.Packages.props` | added only when present |
| `global.json` | added only when present; the repository has none today |

Each file contributes a line of `<path relative to the parent directory of the repository root>` and its
lowercase SHA-256; the records are sorted by full path and the fingerprint is the SHA-256 of the joined
text. Consequences:

- Any file under those entries changes the fingerprint, including a file that is not input to the build.
- The sibling `CSweet.Isolation` directory must exist, or the fingerprint cannot be computed.
- Host code is deliberately not hashed. Changes under `Node`, `RuntimeHost`, `Runtime.LocalRpc`, the
  providers, or the helpers reuse the cached guest image. See [09-guest-image-changes.md](09-guest-image-changes.md).

Cache reuse: the script scans `artifacts\windows-runtime\source` for
`csweet-agent-guest-<fingerprint>*.vhdx.ready`, newest first, at most 32 candidates. It reuses the first
candidate whose `.vhdx` exists and whose `.ready` file contains exactly the current fingerprint. The
marker is written after a successful build as `<image>.vhdx.ready` containing the fingerprint.

`-RebuildGuest` does not delete the previous image. The new build is written to
`csweet-agent-guest-<fingerprint>-<32-hex>.vhdx` so installed or still-referenced disks stay immutable.
Use `Clear-CSweetGeneratedHyperVImages.ps1` (below) to reclaim the space.

## The three published binaries

All three are published `-c Release --self-contained true -p:PublishSingleFile=true`:

| Project | Runtime identifier | Output |
|---|---|---|
| `src\CSweet.Office.Runtime.HyperV.Helper` | `win-x64` | `<runRoot>\helper\CSweet.Office.Runtime.HyperV.Helper.exe` |
| `src\CSweet.Office.GuestProbe` | `linux-x64`, with `IncludeNativeLibrariesForSelfExtract` | `<runRoot>\probe\CSweet.Office.GuestProbe` |
| `src\CSweet.Office.WindowsSmokeTest` | `win-x64` | `<runRoot>\smoke\CSweet.Office.WindowsSmokeTest.exe` |

A missing output throws before the smoke test runs, so a broken publish never produces certification
evidence or a payload.

## The certification smoke run

```
CSweet.Office.WindowsSmokeTest.exe --helper <helper> --guest-image <vhdx> --probe <probe> \
    --output-root <smoke output> --evidence <runRoot>\windows-hyperv.json
```

- All output is captured and written to `<runRoot>\certification.log`; native stderr is captured rather
  than aborting the script.
- The run must exit `0` **and** produce the evidence file, otherwise the script throws with the first
  output line plus the log path.
- The evidence `checks` object must exist and contain only `true` values. One `false` fails the run.
- `certificationSuiteVersion`, `certifiedAt`, and `certificationExpiresAt` are read back from the
  evidence and passed into payload assembly; the expiration is what later fails closed in provider
  selection (`SEC-INV-10`).

## Guest image signing

- Signer subject: `CN=C-Sweet Windows Hyper-V Development Guest Image Signer`, in `Cert:\CurrentUser\My`.
- Reuse rule: an existing certificate with a private key and `NotAfter` more than 30 days away. Otherwise
  `New-SelfSignedCertificate` creates an RSA 3072 / SHA-256 signing certificate valid for one year.
- Export: the certificate is written as `guest-image-signing.cer` into the run root.
- Signature: SHA-256 of the VHDX, signed with the RSA private key using PKCS#1 v1.5 padding, written
  next to the image as `<guestImage>.sig`.

This identity is development-only. Release signing and certification require the hardened platform
workflows (`AGENTS.md`).

## Payload assembly

`New-CSweetWindowsRuntimePayload.ps1` is called with the certified inputs:

| Parameter | Required | Value in the loop |
|---|---|---|
| `-GuestImage` | yes | the signed VHDX |
| `-GuestImageSignature` | yes | `<guestImage>.sig` |
| `-GuestImageSigningCertificate` | yes | `guest-image-signing.cer` |
| `-GuestImageSigningCertificateThumbprint` | yes | the signer thumbprint |
| `-CertificationEvidence` | yes | `windows-hyperv.json` |
| `-CertificationSuiteVersion` | yes | from the evidence |
| `-CertifiedAt` | yes | from the evidence |
| `-CertificationExpiresAt` | no | from the evidence; `null` when absent |
| `-PackageVersion` | no (`dev-<yyyyMMdd-HHmmss>`) | must match `^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$` |
| `-RuntimeIdentifier` | no (`win-x64`) | publish RID |
| `-OutputRoot` | no (`artifacts\windows-runtime\payload`) | must not be a filesystem root |

It publishes `RuntimeHost`, the Hyper-V helper, `Node`, and `Configurator` self-contained, copies the
image, signature, certificate, and evidence into `images/`, `certificates/`, and `certification/`,
refuses to continue when the published Office `FileVersion` is below `0.1.0.0` (the message names
`1.0.2` as the requirement for privileged signed-assignment enforcement), and writes
`runtime-manifest.json` with a SHA-256 for every file in the payload except the manifest itself. See
[60-release/02-payload-and-manifest.md](../60-release/02-payload-and-manifest.md).

## Install

Unless `-SkipInstall` is passed, the script calls
`Install-CSweetOfficeRuntimeHost.ps1 -PayloadRoot <payload> -ControlPlaneUserSid <sid> -ControlPlaneUrl <url> [‑EnrollmentTokenInputPath <path>]`
with the progress parameters, and throws on a non-zero result.

The root `README.md` documents the manual alternative, which is also the pattern to use when you want to
install a specific payload by hand:

```powershell
$payload = Get-ChildItem '.\artifacts\windows-test' -Directory -Filter 'certification-*' |
    Where-Object { Test-Path (Join-Path $_.FullName 'payload\runtime-manifest.json') } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName 'payload' }

$manifest = Get-Content (Join-Path $payload 'runtime-manifest.json') -Raw | ConvertFrom-Json
$node = Get-Item (Join-Path $payload $manifest.officeExecutable)
if ([Version]$node.VersionInfo.FileVersion -lt [Version]'0.1.0.0') {
    throw "Stale payload: Office $($node.VersionInfo.FileVersion)"
}

.\scripts\windows\Install-CSweetOffice.ps1 `
    -PayloadRoot $payload `
    -ControlPlaneUrl 'https://localhost:54782'
```

## The stale-payload rule

Building the solution does not refresh an installed payload. `certification-*` directories accumulate in
`artifacts\windows-test`, and every one of them contains a complete, self-consistent
image+evidence+executables set. Two rules follow:

1. Never select an older `certification-*` payload after a rebuild. The installer verifies the payload
   against its own manifest, so an older payload installs cleanly — with an older guest image, older
   evidence, and older host binaries.
2. The version gate in the README snippet (`FileVersion >= 0.1.0.0`) catches only payloads built from
   Office versions before reliable signed-assignment delivery. It does not catch a payload that is merely
   older than the current source tree.

## Footguns

| Footgun | Consequence |
|---|---|
| The fingerprint excludes host code | A host-only change reuses the cached guest image. That is intended, but it means a green run does not prove the image contains your guest change. |
| The RuntimeHost installer skips digest-matching files | Re-running the installer with a stale payload copies only files whose digests differ, so nothing changes and the run looks successful (`RuntimeHostInstaller_SkipsAlreadyInstalledContentByDigest`). |
| A missing sibling `CSweet.Isolation` checkout | The fingerprint cannot be computed and the guest build cannot run; the failure appears before any build output. |
| Any file under the fingerprinted roots | Invalidates the cached image, including a file you would not consider build input. |
| `-RebuildGuest` | Leaves the previous image on disk. Space grows until `Clear-CSweetGeneratedHyperVImages.ps1` is run. |
| Restart-required exit `0` | Automated callers that treat any exit `0` as "done" will report success while Hyper-V is still pending a restart. Check the progress file state. |

## Cleaning up generated images

`scripts\windows\Clear-CSweetGeneratedHyperVImages.ps1` reclaims disk space without touching the current
image:

- Self-elevates the same way as the initialization script, with `-NoElevation` to refuse.
- Identifies the newest `*.vhdx.ready` marker in `artifacts\windows-runtime\source` and preserves every
  file carrying that image prefix.
- Removes Hyper-V VMs whose disks are all under `artifacts\windows-test` or
  `artifacts\windows-runtime\source`, or whose configuration path is under them.
- Deletes generated VHD/VHDX files leaf-first along the differencing-disk graph, dismounting first where
  the disk is mounted.
- When a delete fails because only the System process holds the file, it schedules deletion on reboot
  through the Windows Restart Manager API and reports it as scheduled instead of failing.
- Writes `artifacts\windows-image-cleanup.log` with a diagnostic JSON (VMs, disks, `vmwp.exe` processes)
  when it fails, and prints a JSON summary on success.

## Sources

`README.md`, `AGENTS.md`, `scripts\windows\Initialize-CSweetWindowsIsolationTest.ps1`,
`scripts\windows\New-CSweetHyperVTestGuest.ps1`, `scripts\windows\New-CSweetWindowsRuntimePayload.ps1`,
`scripts\windows\Clear-CSweetGeneratedHyperVImages.ps1`, `scripts\windows\Install-CSweetOffice.ps1`,
`scripts\windows\Install-CSweetOfficeRuntimeHost.ps1`, `scripts\windows\CSweet.WindowsSetupProgress.ps1`,
`src\CSweet.Office.Runtime.Core\ExternalPlatformIsolationBackend.cs`,
`tests\CSweet.Office.Tests\WindowsHyperVOnboardingTests.cs`.

Verified: 2026-09-15.
