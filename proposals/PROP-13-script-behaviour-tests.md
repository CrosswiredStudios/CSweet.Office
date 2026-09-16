# PROP-13 — Replace script-text assertions with behavioural tests

**Status:** proposal — not implemented. **Area:** quality (installer safety). **Effort:** large.
**Tooling cost:** none for the tests themselves. Avoid editing `scripts/windows/New-CSweetHyperVTestGuest.ps1`
— it is inside the guest-image fingerprint set, so touching it invalidates every developer's cached image.

**Invariant interactions:** the scripts under test enforce `SEC-INV-16` (package ACLs), `SEC-INV-17`
(domain-controller refusal), `SEC-INV-18` (reconnect wipes mutable trust), and `SEC-INV-19` (maintenance
settings). The point of this proposal is to test that enforcement behaviourally, not to change it.

## Problem

Several test classes assert on the **text** of installer and uninstaller scripts, and they pass against a
script that is present but broken:

- [`tests/README.md`](../tests/README.md) and [`test conventions`](../.github/instructions/tests.instructions.md)
  list `LinuxInstallationTests` (asserts `scripts/linux/*.sh` text) and most of `WindowsHyperVOnboardingTests`
  (script text, plus one behavioural case that shells out to Windows PowerShell 5.1).
- [`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md)
  says it plainly: "Several test classes assert on the **text** of scripts rather than their behavior, so
  they pass against a script that is present but broken."
- The one behavioural harness, `scripts/tests/Test-OfficeUpgradeProbe.ps1`, proves the pattern works: it
  stubs the cmdlets the recovery probe uses, builds a fake install tree under
  `%TEMP%\office-probe-test-<guid>`, asserts the eight `Get-CSweetOfficeRecoveryState.ps1` state transitions,
  and exits `0` with `Passed 8 upgrade probe scenarios.` It is invoked as a child process by
  `WindowsHyperVOnboardingTests.UpgradeProbeDistinguishesPreservedStateFromExecutingWork`, and it can be run by
  hand.

Everything else that matters — the drain gate that must refuse an upgrade, the reconnect path that must wipe
identity, the domain-controller refusal, the non-inherited `RX` ACEs, the atomic trust write — is asserted by
string matching. A refactor that renames a variable, reorders two checks, or changes a conditional will keep
the strings present and the suite green while breaking the installer.

## User story

**Scenario.** An installer refactor moves the drain-gate check into a helper function and accidentally drops
the `active-assignments` count from the condition. Every script-text assertion still finds the strings it
looks for, CI is green, and the mistake ships. The next customer upgrade proceeds against an office with
running work.

> As a maintainer, I want the installer's decisions exercised by tests that run the real decision code, so
> that a refactor which breaks the drain gate fails CI instead of failing an upgrade.

## Recommended fix

Generalise the `Test-OfficeUpgradeProbe.ps1` pattern, which already solves the hard parts (stubbing the host,
building a fake tree, asserting exit codes):

1. **Extract decision logic into testable functions.** The gate conditions (drain marker present, zero
   `active-assignments/*.active`), the reconnect wipe list, the domain-controller check, the ACL computation,
   and the atomic trust write should each be callable with injectable inputs (paths, a fake registry reader, a
   stub `icacls`). The scripts stay PowerShell 5.1, `Set-StrictMode -Version Latest`, `$ErrorActionPreference =
   'Stop'`, and keep `-NoElevation` for self-elevation — the extraction is about testability, not rewriting.
2. **Build one harness per decision** under `scripts/tests/`, following the existing script's conventions:
   stub the host cmdlets, build a fake install tree under `%TEMP%`, assert state transitions and exit codes,
   print a `Passed N … scenarios.` summary, exit `0`.
   Priority order: drain gate (upgrade and uninstall), reconnect trust wipe and its refusal when the recovery
   state is `active`, domain-controller refusal, package ACL assertions
   (`Assert-FileReadExecuteAce` accepting and rejecting), atomic trust write (no partial file on failure).
3. **Invoke each harness from the test project** the same way the upgrade probe is invoked today, so they run
   in CI (with [PROP-11](PROP-11-windows-ci-coverage.md)'s Windows job) rather than only by hand.
4. **Keep the text assertions that still earn their keep** — for example, the MSI staging list — but stop
   treating them as behaviour coverage, and update the "what a green run does not prove" tables to say what is
   now behavioural.

Pester is optional. If the team prefers it, use a pinned module version and the same stubbing approach; the
existing hand-rolled harness is the compatibility baseline and needs no new dependency.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `scripts/windows/*.ps1` | Extract decision functions with injectable inputs | Follow [installer conventions](../.github/instructions/installers-and-scripts.instructions.md); never touch `New-CSweetHyperVTestGuest.ps1` |
| `scripts/linux/*.sh`, `scripts/macos/*.sh` | Shell equivalents for the drain/upgrade gates where practical; otherwise document the gap | Shell harnesses can run on the Ubuntu CI job |
| `scripts/tests/*.ps1` (new) | One harness per decision, following `Test-OfficeUpgradeProbe.ps1` | Print a summary line and exit `0`/non-zero |
| `tests/CSweet.Office.Tests/WindowsHyperVOnboardingTests.cs` | Add child-process invocations for each harness; keep or trim text assertions | One behavioural test per harness |
| Docs | [`tests/README.md`](../tests/README.md), [`docs/80-reference/tests.md`](../docs/80-reference/tests.md), [`docs/80-reference/scripts.md`](../docs/80-reference/scripts.md), the two "why tests will not protect you" passages | Refresh `Verified:` dates |

## What must not change

- The invariants the scripts enforce: `SEC-INV-16`, `SEC-INV-17`, `SEC-INV-18`, `SEC-INV-19`.
- PowerShell 5.1 compatibility, `-NoElevation`, atomic writes, and the progress-file path contract.
- The upgrade-probe harness and its eight scenarios — extend the family, do not replace it.

## Verification

1. Seed a break in each extracted decision (drop `active-assignments` from the gate; skip a directory in the
   wipe list) and confirm the harness fails while the old text assertions still pass.
2. Run the full family by hand on Windows and confirm the summary lines and exit codes.
3. Confirm no script in the five guest-image roots has changed (check `Get-GuestBuildFingerprint` before and
   after).

## Sources

`scripts/tests/Test-OfficeUpgradeProbe.ps1`, `scripts/windows/{Install-CSweetOffice.ps1,Install-CSweetOfficeRuntimeHost.ps1,Enter-CSweetOfficeMaintenance.ps1,Get-CSweetOfficeRecoveryState.ps1}`,
`tests/CSweet.Office.Tests/{WindowsHyperVOnboardingTests.cs,LinuxInstallationTests.cs}`, `tests/README.md`,
`.github/instructions/{tests.instructions.md,installers-and-scripts.instructions.md}`,
`docs/20-security/12-known-limits-and-tradeoffs.md`, `docs/80-reference/tests.md`.

Verified: 2026-09-15.
