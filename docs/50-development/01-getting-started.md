# Getting started

**Audience:** a contributor building this repository for the first time.

## Prerequisites

| Requirement | Why it is needed | Notes |
|---|---|---|
| .NET SDK 10 on `PATH` | every build and test run | `Directory.Build.props` sets `TargetFramework` to `net10.0`. There is no `global.json`, so the SDK is whatever `dotnet` resolves from `PATH`. CI pins `10.0.x`. |
| Sibling `CSweet.Office.Contracts` checkout | building `CSweet.Office.slnx` | The primary solution lists the sibling project under the `/dependencies/` solution folder. `Directory.Build.props` defaults `CSweetOfficeContractsRepositoryRoot` to `..\CSweet.Office.Contracts` and auto-detects the project file there. |
| Sibling `CSweet.Isolation` checkout (Windows only) | building the Hyper-V guest image | `New-CSweetHyperVTestGuest.ps1` imports `tools\LinuxImage\CSweet.LinuxImage.psd1` from it; the default `-IsolationRoot` is the sibling in the same parent directory. The directory is part of the guest build fingerprint (`09-guest-image-changes.md`). |
| Administrator rights, Hyper-V, a Hyper-V switch | the Windows loop | `Initialize-CSweetWindowsIsolationTest.ps1` self-elevates, enables `Microsoft-Hyper-V` when missing, and needs a switch (default `Default Switch`). |
| Root, cgroup v2, read/write `/dev/kvm` | the Linux loop | `scripts/linux/initialize-firecracker-test.sh` refuses to start without them. It also needs `curl`, `dotnet`, `jq`, `openssl`, `sha256sum`, and `tar`. |
| macOS 14 or later, Swift toolchain, `codesign`, `plutil`, `python3` | the macOS loop | `Package.swift` declares `swift-tools-version: 5.9` and `platforms: [.macOS(.v14)]`; `scripts/macos/new-runtime-payload.sh` requires `codesign dotnet jq plutil python3 shasum swift`. |
| WiX Toolset v4 (`wix`) and Windows SDK `signtool` | MSI packaging only | `scripts/windows/New-CSweetOfficeMsi.ps1` fails fast when either tool is missing. |

A build also needs no other repository, with one exception: the Independent solution is designed to
build from a lone checkout plus the pinned package, which is what CI does.

## Clone layout

The two sibling directories must sit next to the repository directory. Both defaults compute a sibling,
not a child:

```
<parent>/
├─ CSweet.Office/            this repository
├─ CSweet.Office.Contracts/  required by CSweet.Office.slnx and the switch auto-detect
└─ CSweet.Isolation/         required by the Windows guest-image build
```

- `CSweetOfficeContractsRepositoryRoot` defaults to `$(MSBuildThisFileDirectory)..\CSweet.Office.Contracts`,
  that is, `<parent>\CSweet.Office.Contracts`.
- `New-CSweetHyperVTestGuest.ps1 -IsolationRoot` defaults to `$PSScriptRoot\..\..\..\CSweet.Isolation`,
  which resolves to the same parent directory.

If the siblings live elsewhere, pass `-p:CSweetOfficeContractsRepositoryRoot=<path>` to `dotnet`, or
`-IsolationRoot <path>` to the guest builder. Do not copy the projects into this tree; the switch and
the fingerprint both depend on the sibling paths.

## Build

The two commands from the root `README.md`:

```powershell
dotnet build CSweet.Office.slnx -c Release
dotnet build CSweet.Office.Independent.slnx -c Release -p:UseLocalOfficeContracts=false
```

The first uses the sibling `CSweet.Office.Contracts` project. The second deliberately does not and is
the package boundary that CI and releases exercise. Run the second before pushing a contracts change;
see [03-contracts-dependency.md](03-contracts-dependency.md).

## First test run

```powershell
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release
```

The CI-equivalent sequence, which also proves the package boundary:

```powershell
dotnet restore CSweet.Office.Independent.slnx -p:UseLocalOfficeContracts=false
dotnet build CSweet.Office.Independent.slnx -c Release --no-restore -p:UseLocalOfficeContracts=false
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release --no-build -p:UseLocalOfficeContracts=false
```

The suite is plain xUnit with no external services and no privileged operations; it is safe to run on
any development machine. What it does and does not cover is in [07-testing-guide.md](07-testing-guide.md).

## Installing instead of developing

The operator path for Ubuntu 24.04, from the root `README.md`, installs a signed package and enrolls
through the root-owned configurator:

```bash
sudo apt install ./csweet-office_VERSION_ARCH.deb
sudo csweet-configure-office https://YOUR-C-SWEET-HOST
```

That is not the development loop. To build and certify a payload from source, use
[05-linux-dev-loop.md](05-linux-dev-loop.md) on Linux, [04-windows-dev-loop.md](04-windows-dev-loop.md)
on Windows, or [06-macos-dev-loop.md](06-macos-dev-loop.md) on macOS.

## The first hour

1. Read [`AGENTS.md`](../../AGENTS.md). Its four rules are normative and cover contract versioning,
   signing, and identity preservation.
2. Read [10-system/01-what-is-office.md](../10-system/01-what-is-office.md) and
   [03-components.md](../10-system/03-components.md) — what this repository contains and what remains
   in C-Sweet.
3. Read [10-system/04-solution-map.md](../10-system/04-solution-map.md) and
   [02-build-and-test.md](02-build-and-test.md) — the two solutions and the switch.
4. Build both solutions and run the suite once.
5. Read [20-security/11-security-invariants.md](../20-security/11-security-invariants.md). Treat the
   `SEC-INV-nn` identifiers as the acceptance criteria for anything you touch.
6. Start the loop for your platform: [04](04-windows-dev-loop.md), [05](05-linux-dev-loop.md), or
   [06](06-macos-dev-loop.md).
7. Before touching an isolation provider or the guest image, read
   [08-adding-an-isolation-provider.md](08-adding-an-isolation-provider.md) and
   [09-guest-image-changes.md](09-guest-image-changes.md) first — both have cache and certification
   consequences that are invisible in the source.

## Sources

`README.md`, `AGENTS.md`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
`CSweet.Office.slnx`, `CSweet.Office.Independent.slnx`, `.github/workflows/ci.yml`,
`tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj`, `scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1`,
`scripts/windows/New-CSweetHyperVTestGuest.ps1`, `scripts/windows/New-CSweetOfficeMsi.ps1`,
`scripts/linux/initialize-firecracker-test.sh`, `scripts/macos/new-runtime-payload.sh`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Package.swift`.

Verified: 2026-09-15.
