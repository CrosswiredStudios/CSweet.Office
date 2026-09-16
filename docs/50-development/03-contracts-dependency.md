# The contracts dependency

**Audience:** anyone changing a message that crosses the Office↔Headquarters boundary.

`CSweet.Office.Contracts` is where every cross-repository message shape lives: control-plane calls,
guest envelopes, security primitives, and workload specifications. It is a NuGet package owned by
another repository. This repository consumes it, and CI proves that it can be consumed without a
sibling source checkout.

## Why the package exists

- The root `README.md` states that the Headquarters gateway, scheduling, enrollment approval,
  certificates, artifact authorization and storage, guest broker streaming, and fleet UI remain in
  C-Sweet, and that "cross-repository messages are versioned in `CSweet.Office.Contracts`".
- `Directory.Build.targets` gives the reference to every project except one literally named
  `CSweet.Office.Contracts`. Each of those projects also links `Office.GlobalUsings.cs`, which supplies
  `global using` directives for `CSweet.Office.Contracts.{ControlPlane, Guest, Security, Workloads}`.
- The guest executables (`RuntimeGuest`, `BuilderGuest`, `ToolchainGuest`, `GuestProbe`) have no project
  references on purpose; they compile against the package and the shared global usings so a guest binary
  never drags runtime or provider code into the image.
- `src/CSweet.Office.Runtime.LocalRpc/RuntimeHostProtocolMapper.cs` is the clearest in-repo example of
  the boundary: the signed specification JSON is deserialized into `CSweet.Office.Contracts.Workloads`
  types, then mapped onto the private protobuf contract.

## The pin

The version lives in exactly one place, `Directory.Packages.props`:

```xml
<PackageVersion Include="CSweet.Office.Contracts" Version="0.7.0" />
```

This is independent of `VersionPrefix` (`0.5.4` in `Directory.Build.props`), which is the Office version.
Do not "align" the two numbers; they version different artifacts and are released on their own tags.

## The sibling-repo auto-detect footgun

`UseLocalOfficeContracts` is decided by `Directory.Build.props` (rules 1–4 in
[02-build-and-test.md](02-build-and-test.md)). The practical hazard is rule 3: any checkout that happens
to have `..\CSweet.Office.Contracts` present silently builds against that working tree instead of the
pinned package. Consequences:

- Tests can pass against contract code that has never been released.
- CI cannot catch it because CI has no sibling, so the class of failure you reproduce locally does not
  exist there.
- A restore against the package pin can succeed while the code you ran came from the sibling.

Always use `-p:UseLocalOfficeContracts=false` when the claim under test is about the released package,
and prefer `dotnet restore` over an implicit restore for reproducibility.

## The cross-repository workflow

`AGENTS.md` makes this normative: if `CSweet.Office.Contracts` changes, bump that package using semantic
versioning, pack it, update this repository and C-Sweet to the same released pin, and verify both with
local project references disabled.

The ordered form:

1. Change the Contracts surface in the Contracts repository. Additive changes are minor; changed or
   removed member semantics are major for every consumer, including Headquarters.
2. Bump the package version using semantic versioning and pack it.
3. Publish the package to the feed both repositories restore from. Until it is on a feed, `restore` with
   `-p:UseLocalOfficeContracts=false` fails — that failure is the intended signal that packaging and
   pinning must move together.
4. Update `Directory.Packages.props` in this repository to exactly that version.
5. Update C-Sweet to the same released pin. Both repositories must name the same version; a released pin
   that only one side has is a broken release.
6. Verify both repositories with local project references disabled. Here that means:

```powershell
dotnet build CSweet.Office.Independent.slnx -c Release -p:UseLocalOfficeContracts=false
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release -p:UseLocalOfficeContracts=false
```

> **Out of repo:** the Contracts package source, its versioning, packing, and publishing, and the
> C-Sweet-side pin and verification, are owned by other repositories. The in-repo evidence that this
> boundary exists is `Directory.Build.targets`, `Directory.Packages.props`, `Office.GlobalUsings.cs`,
> and the Independent solution that CI and `scripts/release/Invoke-PlatformRelease.ps1` build.

## Sources

`AGENTS.md`, `README.md`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
`Office.GlobalUsings.cs`, `.github/workflows/ci.yml`, `docs/10-system/04-solution-map.md`,
`src/CSweet.Office.Runtime.LocalRpc/RuntimeHostProtocolMapper.cs`.

Verified: 2026-09-16.
