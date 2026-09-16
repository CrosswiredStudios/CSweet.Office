# PROP-11 — Exercise Windows code paths in CI

**Status:** proposal — not implemented. **Area:** quality (verification coverage). **Effort:** medium.
**Tooling cost:** CI only.

**Invariant interactions:** none. The CI build and test commands, including
`-p:UseLocalOfficeContracts=false`, must not change — that switch is what proves the published package
boundary ([`release-and-ci.instructions.md`](../.github/instructions/release-and-ci.instructions.md)).

## Problem

`.github/workflows/ci.yml` is a single `ubuntu-latest` job: restore and build
`CSweet.Office.Independent.slnx`, then `dotnet test` with `--no-build`. The test conventions say the quiet part
out loud: tests that cannot run on the current OS **early-return** rather than skip, and the
[tests README](../tests/README.md) lists what a green run therefore does not prove on Linux:

- `RuntimeHostRpcIntegrationTests` — exercises the Windows named-pipe transport, the pipe DACL, and the
  server's connection-per-request behaviour. Early-returns off Windows.
- `WindowsHyperVOnboardingTests` — the one behavioural case shells out to Windows PowerShell 5.1, and the rest
  assert installer script text. Early-returns off Windows.
- `OfficeCertificateTlsTests` — takes the `RSACng` path on Windows and `RSA.Create` elsewhere, so the two
  platforms exercise different code.
- The behavioural harness `scripts/tests/Test-OfficeUpgradeProbe.ps1` is a PowerShell script that stubs the
  host; it runs only as part of the Windows-only test above.

Windows is the primary platform (Hyper-V), and it is also the platform whose transport, service model, and
installer are the most Windows-specific. CI never touches any of it. There is also no script linting and no
dependency audit anywhere in the pipeline.

## User story

**Scenario.** A contributor changes the helper's argument parsing and the named-pipe connection handling. CI
is green on Ubuntu — where those tests returned early without executing a single assertion. The change is
merged, and the regression surfaces at the next Windows payload certification, days later, with no clear
suspect.

> As a reviewer, I want CI to exercise the Windows-only paths, so that a green check means the primary
> platform was actually tested.

## Recommended fix

Add a second job to `ci.yml`; keep the existing Ubuntu job as the fast path.

```yaml
  build-test-windows:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet restore CSweet.Office.Independent.slnx -p:UseLocalOfficeContracts=false
      - run: dotnet build CSweet.Office.Independent.slnx -c Release --no-restore -p:UseLocalOfficeContracts=false
      - run: dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release --no-build -p:UseLocalOfficeContracts=false
      - name: Upgrade-probe harness
        shell: powershell
        run: |
          powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\tests\Test-OfficeUpgradeProbe.ps1
```

Then add, as separate steps or jobs:

1. **Script linting.** PSScriptAnalyzer over `scripts/**` with a pinned module version. Start at
   `-Severity Error` and record any accepted suppressions in a checked-in settings file; the scripts are
   PowerShell 5.1 by rule, so lint them with the same host the installers use.
2. **Dependency audit.** `dotnet list CSweet.Office.Independent.slnx package --vulnerable --include-transitive`
   (non-blocking initially, blocking once the baseline is clean).
3. **Coverage visibility (optional).** Collect per-class coverage so the early-return classes are visible as
   uncovered on Linux rather than invisible.

Note the runner rules: the hardened-runner requirement applies to the **release** workflow. A hosted
`windows-latest` runner for CI is consistent with the rules and must never be used for signing or publishing.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `.github/workflows/ci.yml` | Add the Windows job and the lint/audit steps | Keep the explicit `-p:UseLocalOfficeContracts=false` everywhere |
| Tests | Nothing required; confirm the named-pipe tests actually execute on the Windows runner and fail loudly when the transport is broken | If a test silently early-returns on Windows too, fix the guard |
| Docs | [`docs/60-release/04-release-pipeline.md`](../docs/60-release/04-release-pipeline.md) (CI description), [`tests/README.md`](../tests/README.md) (what runs where now), [`docs/50-development/02-build-and-test.md`](../docs/50-development/02-build-and-test.md) | Keep the "a green run does not prove" table honest for the remaining gaps (macOS paths, hypervisor interaction) |

## What must not change

- The independent-solution build and the explicit contracts switch.
- The prohibition on signing or publishing from an ordinary runner; CI gets no release secrets.
- The early-return convention itself — it is intentional; the fix is to run on the platform, not to change
  the convention.

## Verification

1. Open a pull request that intentionally breaks the named-pipe DACL (for example, widen an ACE) and confirm
   the Windows job fails while the Linux job stays green.
2. Confirm the upgrade-probe harness runs and reports `Passed 8 upgrade probe scenarios.`
3. Confirm the lint and audit steps fail on a seeded problem, then pass clean.

## Sources

`.github/workflows/ci.yml`, `.github/instructions/release-and-ci.instructions.md`, `tests/README.md`,
`docs/50-development/02-build-and-test.md`, `scripts/tests/Test-OfficeUpgradeProbe.ps1`,
`docs/60-release/04-release-pipeline.md`.

Verified: 2026-09-15.
