# PROP-12 — Add `.editorconfig` and analyzer enforcement

**Status:** proposal — not implemented. **Area:** quality (consistency, defect prevention). **Effort:** small
to start, medium once the warning backlog is drained.

**Tooling cost:** a new root `.editorconfig` is not part of the guest-image fingerprint. Changing
`Directory.Build.props` **is** (`Get-GuestBuildFingerprint` hashes it), so that edit invalidates every
developer's cached Windows guest image once — batch it with another props change, such as the next
`VersionPrefix` bump, which invalidates the cache anyway.

**Invariant interactions:** none. This is build hygiene.

## Problem

The build enables style enforcement without a policy to enforce:

- `Directory.Build.props` sets `EnforceCodeStyleInBuild` to `true`, but the repository has no `.editorconfig`
  anywhere ([`docs/10-system/04-solution-map.md`](../docs/10-system/04-solution-map.md) already records the
  gap: "analyzer style rules run against the compiler defaults rather than a project-specific policy").
- No `AnalysisLevel`, no `TreatWarningsAsErrors`, and no analyzer package references: warnings are advisory,
  and nothing stops a merge that adds them.
- The C# conventions that do exist — nullable, sealed records, `TimeProvider`, primary constructors, central
  package management — live in [`cs-code.instructions.md`](../.github/instructions/cs-code.instructions.md) and
  [`docs/50-development/10-code-conventions.md`](../docs/50-development/10-code-conventions.md) and are
  enforced by review, not by the compiler.

For a repository where every file in `Runtime.LocalRpc` and `Runtime.Protocol` is "either a security control
or a parser sitting next to one", mechanical drift is review load that should be spent on the boundary code.

## User story

**Scenario.** Two contributors add similar helpers to `Runtime.Core` in the same week. One uses
`TimeProvider`, one calls `DateTimeOffset.UtcNow`; one passes named arguments, the other relies on positional
booleans in the provider catalog. Reviewers catch neither, because nothing pips. Six months later the
provider catalog needs an argument added, and the positional entries become a counting exercise with a
security-relevant descriptor.

> As a contributor, I want mechanical style and analyzer rules enforced by the build, so that review time
> goes to the security boundary rather than to formatting and idiom drift.

## Recommended fix

1. **Add a root `.editorconfig`** covering C# (`indent_size = 4`, `charset = utf-8`, file-scoped namespaces,
   `csharp_style_*` preferences at `suggestion`, naming styles for private fields and constants) and markdown
   hygiene for the documentation tree. Start conservative: severities at `suggestion`/`warning`, with the few
   rules that catch real defects at `error`.
2. **Enable the .NET analyzers** with `AnalysisLevel` (start at `latest-recommended`) and
   `TreatWarningsAsErrors` in `Directory.Build.props`. Because that file is fingerprinted, do this in a change
   that also carries the next `VersionPrefix` bump, and say so in the pull request — the deliverable is one
   guest rebuild, not two. Alternatively, land the analyzer properties in `Directory.Build.targets`, which is
   not fingerprinted; that is legitimate but must be a deliberate, documented choice rather than an
   accident of the fingerprint list.
3. **Relax the rules where the conventions are deliberate**, not where they are sloppy: the test project
   early-returns by design, script-text assertions are intentional, and the positional provider-catalog
   entries are a documented hazard with a stated preference ("prefer adding names"). Add `.editorconfig`
   overrides for `tests/**` rather than suppressing per file.
4. **Land it green.** The first run will produce a warning wave. Either fix the wave in the same change or
   land with a checked-in `NoWarn`/severity baseline and a task list to burn it down; do not land a permanently
   red build.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `.editorconfig` (new, repository root) | The style policy | Not fingerprinted |
| `Directory.Build.props` or `Directory.Build.targets` | `AnalysisLevel`, `TreatWarningsAsErrors`, optional analyzer package via `Directory.Packages.props` | The props file is fingerprinted; batch the change |
| `tests/**` | `.editorconfig` overrides and any warning fixes | Keep the conventions in [`tests.instructions.md`](../.github/instructions/tests.instructions.md) |
| Docs | [`docs/10-system/04-solution-map.md`](../docs/10-system/04-solution-map.md) (remove the gap note), [`docs/50-development/10-code-conventions.md`](../docs/50-development/10-code-conventions.md) (point at the file) | |

## What must not change

- Central package management: any analyzer package gets a `PackageVersion` in `Directory.Packages.props`,
  never a version on the `PackageReference`
  ([`AGENTS.md`](../AGENTS.md) hard rule 7).
- The deliberate test conventions (early-return, exact-string assertions).
- The fingerprint discipline: do not "fix" the fingerprint list to avoid the rebuild.

## Verification

1. `dotnet build CSweet.Office.Independent.slnx -c Release -p:UseLocalOfficeContracts=false` is clean on a
   fresh clone.
2. A seeded violation (an unused variable, a tab indent, a missing `sealed`) fails the build.
3. CI (including the Windows job from [PROP-11](PROP-11-windows-ci-coverage.md)) stays green.

## Sources

`Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
`docs/10-system/04-solution-map.md`, `docs/50-development/10-code-conventions.md`,
`.github/instructions/cs-code.instructions.md`, `.github/instructions/tests.instructions.md`,
`scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1` (`Get-GuestBuildFingerprint`).

Verified: 2026-09-15.
