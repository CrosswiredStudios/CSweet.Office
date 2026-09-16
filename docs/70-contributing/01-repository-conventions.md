# Repository conventions

**Audience:** everyone opening a change against this repository, including automated agents.

`AGENTS.md` at the repository root holds the four normative rules. They are reproduced here in full because
every other convention on this page follows from them.

## The four normative rules

1. > This repository is an independently versioned installable deliverable. Do not couple its tags to C-Sweet
   > headquarters tags.

2. > If `CSweet.Office.Contracts` changes, bump that package using semantic versioning, pack it, update this
   > repository and C-Sweet to the same released pin, and verify both with local project references disabled.

3. > Never publish or sign from an ordinary development runner. Release signing and certification require the
   > hardened platform workflows.

4. > Preserve Office identity on upgrades only after the office is drained and has zero active assignments. A
   > first install removes legacy Execution Node services but always enrolls a fresh identity.

Rules 3 and 4 also exist as invariants: `SEC-INV-18` (identity is never migrated; reconnect wipes mutable
trust) in [../20-security/11-security-invariants.md](../20-security/11-security-invariants.md). Cite the
invariant rather than paraphrasing it.

## How a change reaches `main`

`.github/workflows/ci.yml` is the only automation that runs on ordinary changes:

| Property | Value |
|---|---|
| Triggers | `pull_request`, and `push` to `main` |
| Job | `build-test` on `ubuntu-latest`, .NET `10.0.x` |
| Steps | `restore`, then `build -c Release --no-restore`, then `dotnet test … --no-build` |

Expectations that follow:

- A change lands through a pull request and must pass `ci`. A push to `main` runs the same job, so `main` is
  expected to be green at all times.
- **CI must pass with `-p:UseLocalOfficeContracts=false`.** All three CI commands pass it. Do not weaken that
  flag to make a change build: if the independent solution fails under the published package, the package
  boundary is what is broken. See `docs/10-system/04-solution-map.md`.
- The tests are the closest thing this repository has to a written specification for the security-critical
  paths. `docs/80-reference/tests.md` (planned) maps test classes to invariants; until it exists, use the
  `Pinned by` references in [../20-security/11-security-invariants.md](../20-security/11-security-invariants.md)
  and the class names in [04-review-checklist.md](04-review-checklist.md).
- There is no `.github` directory content other than `workflows/`: no PR template, no `CODEOWNERS`, no
  `copilot-instructions.md`, and no root `CONTRIBUTING.md`. This page and `AGENTS.md` are the contract.

## Documentation obligations

- Documentation lives in `docs/`. A change that alters observable behaviour, configuration, installation,
  upgrade, or release mechanics must update the affected page in the same change.
- Every page ends with `## Sources` and a `Verified:` date. When you change a page, refresh the date;
  see [02-documentation-style.md](02-documentation-style.md) for the full rules.
- Security behaviour is described once. Invariants are defined in
  [../20-security/11-security-invariants.md](../20-security/11-security-invariants.md) and cited by identifier
  everywhere else.
- Behaviour that belongs to C-Sweet, `CSweet.Office.Contracts`, or `CSweet.Isolation` is marked with an
  `> **Out of repo:**` callout and is never inferred. State the in-repo evidence that establishes the boundary
  instead.

## Files that must not be added

`scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1` computes a bundle fingerprint over a fixed set of
roots and files. Adding, removing, or editing any file under them changes the fingerprint and forces a full
guest-image rebuild on the next Windows development run.

| Fingerprinted root | Kind |
|---|---|
| `src/CSweet.Office.RuntimeGuest` | Directory |
| `src/CSweet.Office.BuilderGuest` | Directory |
| `src/CSweet.Office.ToolchainGuest` | Directory |
| `src/CSweet.Office.Runtime.Protocol` | Directory |
| `build/windows-hyperv` | Directory |
| `scripts/windows/New-CSweetHyperVTestGuest.ps1` | File |
| `Directory.Build.props` | File |
| `Directory.Packages.props` | File |
| `global.json` | File, when present |
| `../CSweet.Isolation/tools/LinuxImage` | Sibling repository directory |

Consequences:

- Do not add a README, an instruction file, a script, or any other artifact under `build/windows-hyperv` or
  under the four `src` roots above. Documentation for those projects belongs in `docs/`.
- Editing `Directory.Build.props` — including a `VersionPrefix` bump for a release — changes the fingerprint.
  That is expected; it is also why a version bump schedules a guest-image rebuild the next time someone runs
  the Windows development loop.
- The enumeration excludes `bin` and `obj` directories, so build output does not invalidate the cache.

The fingerprint and the cache rule are documented in `docs/30-workloads/06-guest-images.md`.

## What a change is allowed to touch

| Area | Convention |
|---|---|
| Package versions | Every version lives in `Directory.Packages.props`. A project adds a `PackageReference` without a version and a `PackageVersion` here — central package management with transitive pinning is enabled. |
| Solutions | Two solutions exist over the same tree. Add a project to both unless you understand why the asymmetry is intentional (`CSweet.Office.ToolchainGuest` is absent from the independent solution but is still built transitively by the test project). |
| Releases | Do not produce signed assets outside the hardened workflow. Development payloads are unsigned build artifacts under `artifacts/`; they are never published. |
| Tests | Tests live in the single project `tests/CSweet.Office.Tests`; script-content assertions read repository files by path, so moving or renaming a script breaks tests as well as documentation. |
| Secrets | No key, password, thumbprint, token, or enrollment secret is committed. Release inputs are protected environment variables; see [../60-release/04-release-pipeline.md](../60-release/04-release-pipeline.md). |

## Sources

`AGENTS.md`, `.github/workflows/ci.yml`, `scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1`,
`Directory.Build.props`, `Directory.Packages.props`, `docs/10-system/04-solution-map.md`,
`docs/30-workloads/06-guest-images.md`, `docs/20-security/11-security-invariants.md`, `CSweet.Office.Independent.slnx`.

Verified: 2026-09-15.
