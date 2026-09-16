# Build and test

**Audience:** any contributor. This page is the authoritative description of how this repository builds
and how the test suite is run.

## Two solutions

| | `CSweet.Office.slnx` | `CSweet.Office.Independent.slnx` |
|---|---|---|
| Purpose | Local development | CI and release boundary proof |
| `CSweet.Office.Contracts` | Sibling project under `/dependencies/` | Absent — resolved as the pinned NuGet package |
| `CSweet.Office.ToolchainGuest` | Listed | Not listed (reached transitively through the test project) |
| Requires a sibling checkout | Yes | No |

```powershell
dotnet build CSweet.Office.slnx -c Release
dotnet build CSweet.Office.Independent.slnx -c Release -p:UseLocalOfficeContracts=false
```

`.github/workflows/ci.yml` always builds and tests the Independent solution with
`-p:UseLocalOfficeContracts=false`, on `ubuntu-latest` with `dotnet-version: '10.0.x'`:

```yaml
- run: dotnet restore CSweet.Office.Independent.slnx -p:UseLocalOfficeContracts=false
- run: dotnet build CSweet.Office.Independent.slnx -c Release --no-restore -p:UseLocalOfficeContracts=false
- run: dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release --no-build -p:UseLocalOfficeContracts=false
```

## The `UseLocalOfficeContracts` switch

`Directory.Build.props` sets the switch; `Directory.Build.targets` consumes it. There are four rules,
in evaluation order:

1. `CSweetOfficeContractsRepositoryRoot` defaults to `$(MSBuildThisFileDirectory)..\CSweet.Office.Contracts`
   and can be overridden on the command line.
2. An explicit `-p:UseLocalOfficeContracts=…` always wins. Both auto-detect conditions require the
   property to be empty before they apply.
3. Otherwise `UseLocalOfficeContracts` becomes `true` only when
   `$(CSweetOfficeContractsRepositoryRoot)\src\CSweet.Office.Contracts\CSweet.Office.Contracts.csproj`
   exists on disk.
4. Otherwise it stays `false`, and every project except one literally named `CSweet.Office.Contracts`
   receives a `PackageReference` to the pinned package instead of a `ProjectReference`, plus a
   `Compile` link to `Office.GlobalUsings.cs`.

```xml
<ItemGroup Condition="'$(MSBuildProjectName)' != 'CSweet.Office.Contracts'">
  <ProjectReference Condition="'$(UseLocalOfficeContracts)' == 'true'"
                    Include="$(CSweetOfficeContractsRepositoryRoot)\src\CSweet.Office.Contracts\CSweet.Office.Contracts.csproj" />
  <PackageReference Condition="'$(UseLocalOfficeContracts)' != 'true'"
                    Include="CSweet.Office.Contracts" />
  <Compile Include="$(MSBuildThisFileDirectory)Office.GlobalUsings.cs" Link="Office.GlobalUsings.cs" />
</ItemGroup>
```

`Office.GlobalUsings.cs` supplies the four `global using` directives for
`CSweet.Office.Contracts.{ControlPlane, Guest, Security, Workloads}`. Because the switch is evaluated per
project, a stray local project file never leaks into a package-boundary build.

> **Footgun.** Rule 3 is silent. If the sibling exists but is stale, dirty, or checked out on the wrong
> branch, the primary solution tests contract code that is not in the released package. Pass
> `-p:UseLocalOfficeContracts=false` whenever the claim you are testing is about the package boundary.

## Central package management

`Directory.Packages.props` sets `ManagePackageVersionsCentrally` and
`CentralPackageTransitivePinningEnabled` to `true` and is the only place a version may appear.

Adding a dependency takes two edits: a version-less reference in the project, and the version in
`Directory.Packages.props`.

```xml
<!-- in the .csproj -->
<PackageReference Include="Some.Package" />

<!-- in Directory.Packages.props -->
<PackageVersion Include="Some.Package" Version="X.Y.Z" />
```

Private build tooling uses `PrivateAssets="all"` on the reference (`xunit.runner.visualstudio` is the
example in this repository). The current pins are:

| Package | Version |
|---|---|
| `CSweet.Office.Contracts` | 0.7.0 |
| `Google.Protobuf` | 3.35.1 |
| `Grpc.Core.Api`, `Grpc.Net.Client`, `Grpc.Tools` | 2.83.0 |
| `Microsoft.Extensions.Hosting`, `.Hosting.Systemd`, `.Hosting.WindowsServices`, `.Http`, `.Logging.Abstractions` | 10.0.9 |
| `Microsoft.NET.Test.Sdk` | 18.7.0 |
| `xunit` | 2.9.3 |
| `xunit.runner.visualstudio` | 3.1.5 |

> **Consequence.** `Directory.Packages.props` is one of the files hashed by the Hyper-V guest build
> fingerprint. A version bump for a package that a guest project uses rebuilds the guest image. See
> [09-guest-image-changes.md](09-guest-image-changes.md).

## Running tests

From a normal checkout:

```powershell
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release
```

Against the package boundary, exactly as CI does it:

```powershell
dotnet restore CSweet.Office.Independent.slnx -p:UseLocalOfficeContracts=false
dotnet build CSweet.Office.Independent.slnx -c Release --no-restore -p:UseLocalOfficeContracts=false
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release --no-build -p:UseLocalOfficeContracts=false
```

The `--no-build` run only works when the preceding build was done with the same switch value, because
the restore graph differs; `dotnet test` with no `--no-build` is the safe default locally.

## Test framework facts

- One flat project, `tests/CSweet.Office.Tests`, 25 test classes, no sub-folders and no shared
  collection fixtures beyond the per-class `IDisposable` pattern.
- xUnit.net v2 (`xunit` 2.9.3) with `Microsoft.NET.Test.Sdk` 18.7.0 and
  `xunit.runner.visualstudio` 3.1.5. The project sets `IsTestProject` and `IsPackable=false` and enables
  the `Xunit` namespace through a global `Using`.
- Only `[Fact]`, `[Theory]`, and `[InlineData]` are used. There is no `[Trait]`, no `SkippableFact`, no
  `[Fact(Skip = …)]`, and no conditional compilation around platform-specific tests — those `return`
  early instead (see [07-testing-guide.md](07-testing-guide.md)).
- The test project references 14 projects directly, including `CSweet.Office.ToolchainGuest`, which is
  absent from the Independent solution. The transitive reference through the test project is what keeps
  the CI `--no-build` step green; do not "fix" that asymmetry casually.
- Several `InternalsVisibleTo` declarations in `src/**/*.csproj` exist so the suite can call internal
  members without widening public surface.

## What the unit suite does not cover

| Not covered | Why |
|---|---|
| Real VM lifecycles on any provider | No test creates, starts, or destroys a VM. The three provider tests only assert the fail-closed probe result when nothing is installed (`PlatformIsolationBackendTests`). |
| Installer and service execution | Nothing runs `Install-CSweetOffice*.ps1`, `install-office.sh`, launchd plists, or systemd units. Windows installer scripts are asserted as text. |
| MSI and native package production | `New-CSweetOfficeMsi.ps1` and `new-native-packages.sh` need WiX/signtool and `dpkg`; only their text is read. |
| Guest image builds | The Packer/Hyper-V build and `new-firecracker-guest.sh` need root, Hyper-V, or `debootstrap`; they run in the development loops, not in tests. |
| Real Headquarters traffic | TLS tests use in-process certificates and a stub server. Enrollment, assignment delivery, and revocation are exercised against C-Sweet, not here. |
| The guest broker handshake end to end | The host side of that handshake is certified by the smoke runner (`CSweet.Office.WindowsSmokeTest`), not by the unit suite. |
| Any cross-repository behavior | Contract shape changes are validated only by building against the pinned package. |

## Sources

`Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `Office.GlobalUsings.cs`,
`CSweet.Office.slnx`, `CSweet.Office.Independent.slnx`, `.github/workflows/ci.yml`,
`tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj`, `docs/10-system/04-solution-map.md`,
`src/CSweet.Office.Runtime.AppleVirtualization.Helper/Package.swift`, `scripts/windows/New-CSweetOfficeMsi.ps1`.

Verified: 2026-09-15.
