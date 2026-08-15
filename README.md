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

## Releases

Office follows semantic versioning and has independent `vX.Y.Z` tags. GitHub Releases is the canonical immutable asset origin; c-sweet.com links to those assets. Office never self-updates. Administrators drain an office to zero active assignments before running an upgrade.
