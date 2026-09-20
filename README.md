# C-Sweet Office

C-Sweet Office is the independently installed execution plane for C-Sweet agents. It runs the unprivileged control client (`CSweet.Office.Node`) and privileged virtualization service (`CSweet.Office.RuntimeHost`) on Windows, Linux, and macOS.

On Windows, both services run under separate Windows-managed virtual accounts (`NT SERVICE\CSweet.Office.RuntimeHost` and `NT SERVICE\CSweet.Office.Node`) with protected state directories. Only the RuntimeHost virtual account is added to `Hyper-V Administrators`; the Node cannot manage Hyper-V or modify RuntimeHost state. Neither account has write access to the immutable application package. Uninstall removes the RuntimeHost membership. Because Windows grants Hyper-V administrators control over every VM on a host, use a dedicated Office machine when unrelated or higher-trust Hyper-V workloads are present. Installation is refused on domain controllers.

The installer pins Headquarters assignment trust over the verified TLS connection before enrollment.
RuntimeHost independently verifies every complete signed workload authorization and commits its fencing
epoch to a protected replay ledger before it asks a platform provider to create a VM. The authorization
binds the Office, assignment, workload, provider, canonical specification digest, issue time,
expiry, and fencing epoch. The unprivileged Node cannot replace the pinned key or replay an accepted
authorization. Existing pre-cutover state is intentionally not compatible; uninstall it and enroll again.

Mixed-use personal machines are supported, but the host administrator and host operating system remain
trusted. A dedicated, patched host provides better assurance. An unavailable or uncertified provider
always fails closed. Development-only execution is not an automatic fallback and must never use production
credentials or sensitive data.

Office reports an explicit `baseline`, `hardened`, or `development` posture. Development
placement requires two independent approvals: the Office installer must opt in and the signed workload
request from Headquarters must opt in. It still requires an available certified hardware-virtualization
provider; there is no process or shared-kernel-container fallback.

This repository was extracted from C-Sweet commit `a85a19be588c82b1d7a6b9b4ed174bf3cd204409`. The import contains the former execution node, RuntimeHost, runtime abstractions and providers, native helpers, builder/runtime guests, payload tools, installer scripts, and certification utilities. Earlier history remains authoritative in the C-Sweet repository.

The headquarters gateway, scheduling, enrollment approval, certificates, artifact authorization/storage, guest broker streaming, and fleet UI remain in C-Sweet. Cross-repository messages are versioned in `CSweet.Office.Contracts`.

## Documentation

[`docs/`](docs/README.md) is the reference documentation for the whole execution plane. Start with
[What C-Sweet Office is](docs/10-system/01-what-is-office.md) for scope, then pick a reading path from the
[documentation index](docs/README.md#reading-paths).

| If you need to | Read |
|---|---|
| Understand the trust chain and privilege split | [Trust model](docs/10-system/02-trust-model.md), [Host privilege model](docs/20-security/06-host-privilege-model.md) |
| Know what must never change | [Security invariants](docs/20-security/11-security-invariants.md) |
| Follow an assignment end to end | [Runtime workload lifecycle](docs/30-workloads/02-runtime-workload-lifecycle.md) |
| Build, test, and run a local payload | [Getting started](docs/50-development/01-getting-started.md), [Windows development loop](docs/50-development/04-windows-dev-loop.md) |
| Install, upgrade, drain, or recover an Office | [Operations](docs/40-operations/README.md) |
| Cut a release | [Release pipeline](docs/60-release/04-release-pipeline.md) |

Contributor instructions that apply to every change, including the contract-versioning and
identity-preservation rules, live in [`AGENTS.md`](AGENTS.md).

## Prebuilt development releases

Windows x64 and Ubuntu 24.04 x64 bundles are built by the GitHub-hosted tagged release workflow.
They include prebuilt guest images and self-contained executables; the destination host performs
isolation certification and development signing. No production signing key is required.
See [hosted development bundles](docs/60-release/07-hosted-development-bundles.md) for release and
installation instructions. The guided C-Sweet Windows setup prefers these bundles and falls back
to local source when no compatible release is available.

## Development

```powershell
dotnet build CSweet.Office.slnx -c Release
dotnet build CSweet.Office.Independent.slnx -c Release -p:UseLocalOfficeContracts=false
```

The primary solution loads the sibling `CSweet.Office.Contracts` project for Visual Studio development. The independent solution intentionally excludes that project and verifies the published package boundary used by CI and releases.

Install Office independently, create a one-use enrollment in C-Sweet, connect it to the gateway, verify its fingerprint, and approve it. C-Sweet AppHost does not launch Office.

### Windows development installation

Building the solution does not refresh an existing installable payload. The installer consumes the exact self-contained executables under the selected payload directory. After host-code changes, create a new certified payload before reinstalling:

```powershell
.\scripts\windows\Initialize-CSweetWindowsIsolationTest.ps1

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

Never select an older `certification-*` payload after rebuilding. The installer rejects payloads whose embedded Office predates reliable signed-assignment delivery.

### Ubuntu 24.04 installation

Linux Office packages support Ubuntu Server/Desktop 24.04 LTS on x64 and arm64. The host must use
cgroup v2 and expose `/dev/kvm` with hardware virtualization enabled. Install the signed package shown
by C-Sweet, then run the root-owned configurator with the Headquarters URL displayed in C-Sweet:

```bash
sudo apt install ./csweet-office_VERSION_ARCH.deb
sudo csweet-configure-office https://YOUR-C-SWEET-HOST
```

The configurator displays the detected security label and its implications before asking for the
one-use connection code. `Baseline` is informational and supports C-Sweet and Office on the same
personal machine. For a dedicated host, use:

```bash
sudo csweet-configure-office https://YOUR-C-SWEET-HOST --dedicated-host
```

For non-interactive Baseline provisioning, explicitly acknowledge the notice with
`--accept-baseline-risk` and provide the connection code on standard input. Enrollment tokens must not
be placed in command-line arguments. Upgrades require the Office to be draining with zero active
assignments. Removal uses `sudo csweet-uninstall-office` after draining, or `--force` only after the
Office is revoked and disposable work may be destroyed.

## Releases

Office follows semantic versioning and has independent `vX.Y.Z` tags. GitHub Releases is the canonical immutable asset origin; c-sweet.com links to those assets. Office never self-updates. Administrators drain an office to zero active assignments before running an upgrade.

## Unattended certificate lifecycle (Office 0.4.0)

Office renews its short-lived connection certificate while running, without interrupting active work. If a certificate expires during downtime, Office proves possession of its original enrolled private key with a one-use, two-minute headquarters challenge. The server still requires an approved, non-revoked enrollment; spent enrollment receipts cannot recover identity. Server TLS pinning, hostname validation, and certificate time checks remain enforced.

Deploy the headquarters recovery endpoints and Office.Contracts 0.6.0 before upgrading Office. Drain and reach zero active assignments before an identity-preserving upgrade. Keep the existing protected identity files; approved Offices with their original key do not need re-enrollment. Revoked or key-lost Offices require administrator intervention. Multi-gateway installations need connection affinity for the challenge/recovery exchange. New TLS connections require the current operational certificate, and revocation is checked throughout existing sessions.

## Guided Windows recovery (Office 0.5.0)

Manage an Office from C-Sweet Settings > Offices > Repair connection. The separate Windows recovery service remains available when the workload service is stopped or its connection certificate has expired. Requests are bound to the enrolled machine and require an administrator action in C-Sweet. The service authenticates using the original enrolled identity; revocation still blocks access.

Repair pauses dispatch and independently checks local work before stopping services. It reinstalls the verified local payload while retaining identity, pool and capacity. To install a newer version, install its signed recovery package on the Office computer first. Existing 0.3/0.4 installations need this one-time local package update and Windows administrator approval before remote recovery becomes available. No inbound management port is opened.

A failed repair exposes a separate, explicit removal and reinstall flow. Removal is never executed unattended by the recovery service. Diagnostic details are available in the C-Sweet guide.

### Shared Ubuntu image builder

The Windows Office image adapter now uses the CSweet.LinuxImage 1.0.0 PowerShell module in the sibling CSweet.Isolation repository. Generic compute uses the same builder with a separate guest software profile. See [shared image service](../CSweet.Isolation/tools/LinuxImage/README.md). Existing Office images are preserved; build fingerprints include shared builder changes. Source builds require that sibling repository, or an explicit IsolationRoot when invoking New-CSweetHyperVTestGuest.ps1.

