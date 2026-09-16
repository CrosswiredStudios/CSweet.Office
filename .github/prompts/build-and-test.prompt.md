---
description: "Build and test C-Sweet Office: build both solutions, run the xUnit suite against the published package boundary, and report what a green run does and does not prove."
mode: agent
---

# Build and test C-Sweet Office

Follow the steps in order and report the actual output, not a summary of intent.

## 1. Confirm the environment

Check whether a sibling `../CSweet.Office.Contracts` checkout exists. If it does, `UseLocalOfficeContracts`
will silently default to `true` — which means an unqualified build tests a local contracts working tree rather
than the released package. Always pass the switch explicitly when the goal is to validate the boundary.

## 2. Build the release boundary

```powershell
dotnet build CSweet.Office.Independent.slnx -c Release -p:UseLocalOfficeContracts=false
```

Report failures verbatim. A missing package (`NU1101` for `CSweet.Office.Contracts`) means the feed does not
have the pinned version — check `Directory.Packages.props` before changing anything.

If a sibling Contracts checkout exists and the change touches contracts, also build the primary solution:

```powershell
dotnet build CSweet.Office.slnx -c Release
```

## 3. Run the tests

```powershell
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release -p:UseLocalOfficeContracts=false
```

## 4. Report honestly

State all of the following:

- Which command produced which result.
- That several classes assert on **script text**, so they pass against a present-but-broken script
  (`LinuxInstallationTests`, most of `WindowsHyperVOnboardingTests`). A green suite is not evidence a script
  works.
- That some tests early-return off-Windows (`RuntimeHostRpcIntegrationTests` and others), so a green run on
  Linux or macOS does not mean those paths were exercised.
- Which security invariants would need privileged or platform-specific verification that this run cannot
  provide.

Read [`docs/50-development/07-testing-guide.md`](../../docs/50-development/07-testing-guide.md) if anything above
is unclear, and [`docs/50-development/02-build-and-test.md`](../../docs/50-development/02-build-and-test.md) for
the full build reference.
