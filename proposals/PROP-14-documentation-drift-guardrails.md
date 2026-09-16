# PROP-14 — Add documentation drift guardrails

**Status:** proposal — not implemented. **Area:** quality (documentation accuracy). **Effort:** small to
medium. **Tooling cost:** a lint script and a CI step; no product impact.

**Invariant interactions:** none. This strengthens
[`docs.instructions.md`](../.github/instructions/docs.instructions.md), whose rules ("cite a source file for
every behavioral claim", "carry a Verified date", "cite SEC-INV identifiers") are currently enforced by
review alone.

## Problem

The documentation set is large, normative, and trusted by operators. It also drifts, and nothing detects it.
Three live examples found while writing this proposal series:

1. **Identity drift.** [`docs/20-security/06-host-privilege-model.md`](../docs/20-security/06-host-privilege-model.md)
   states that both macOS services run as `root`, and its comparison table lists the macOS Node identity as
   `root`. `scripts/macos/com.csweet.office.node.plist` actually sets `UserName _csweetnode` / `GroupName
   _csweet`; only the runtime plist runs as root.
2. **Version drift.** The root [`README.md`](../README.md) tells readers to deploy "Office.Contracts 0.6.0"
   before upgrading, while `Directory.Packages.props` pins `CSweet.Office.Contracts` at 0.7.0.
3. **Rationale drift.** [`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md)
   says builder VMs have "no reliable lease to key on", but the Hyper-V helper records
   `workload.BrokerLease.ExpiresAt` in `instance.json` for every kind (see
   [PROP-06](PROP-06-builder-reap-policy.md)).

Each is small. Together they are the failure mode the documentation style rules exist to prevent: an operator
acts on a page, and the page is wrong. The `Verified:` date is the mechanism for keeping pages honest, but
nothing checks that it is present, current, or consistent with the code it cites.

## User story

**Scenario.** An operator follows the macOS privilege page when writing a backup exclusion list, grants the
wrong identity rights, and leaves the Node's state directory readable by a group it should not be. The page
said `root`; the plist says `_csweetnode`; nothing in CI noticed the disagreement.

> As an operator, I want the documentation constantly checked against the code and the pins, so that the
> guidance I act on matches the system that is actually installed.

## Recommended fix

Add a small, dependency-free lint script plus a CI step, and fix the known drifts in the same change.

1. **`Verified:` presence and shape.** Every page under `docs/**` (and the root `README.md`) must carry a
   `Verified: YYYY-MM-DD.` line. Report missing or malformed dates as errors. Optionally warn when a page has
   been `Verified:` for longer than N months while the files in its `## Sources` list have changed since that
   date — that is the strongest automated signal without semantic analysis.
2. **Pin consistency.** Parse `VersionPrefix` from `Directory.Build.props` and the
   `CSweet.Office.Contracts` `PackageVersion` from `Directory.Packages.props`, then check explicit version
   mentions in `README.md` and `docs/**` (patterns such as `Office.Contracts <version>` and `Office <version>`)
   against them. Report file and line on mismatch. Keep the pattern list short and explicit — a fuzzy matcher
   will be ignored.
3. **Relative links resolve.** Check markdown links of the form `](path)` that resolve inside the repository
   (`../docs/…`, `../../src/…`). Skip links that are repo-external by construction, for example the
   `..\CSweet.Office.Contracts\…` source citations used on the 80-reference pages.
4. **Source-path existence (optional).** For each file named in a `## Sources` list that resolves to a real
   repository path, check it exists. Skip sibling-repository paths and wildcard entries.
5. **Fix the three known drifts now** and refresh the affected `Verified:` dates. If a page cannot be
   reconciled immediately, record the discrepancy in the page rather than leaving the claim silently wrong —
   that is already the documented obligation.

Run the lint from the Ubuntu CI job (it is `pwsh`-compatible and needs no Windows APIs); keep the script
PowerShell 5.1-compatible anyway so it can be run by hand on a Windows host without a second interpreter.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `scripts/tests/Test-OfficeDocumentation.ps1` (new) | The four checks, with a `-Fix`-free, report-only contract | Match the style of `scripts/tests/Test-OfficeUpgradeProbe.ps1` |
| `.github/workflows/ci.yml` | Run the lint | Add to the Ubuntu job; non-zero exit fails the build |
| `docs/20-security/06-host-privilege-model.md` | Correct the macOS identity statements and comparison table | Fix in the same change |
| `README.md` | Correct the Contracts version referenced in the 0.4.0 section | Fix in the same change |
| `docs/20-security/12-known-limits-and-tradeoffs.md` | Correct or remove the builder-lease rationale | Coordinate with [PROP-06](PROP-06-builder-reap-policy.md) |
| `.github/instructions/docs.instructions.md` | Mention the lint so future pages are written to pass it | |

## What must not change

- The documentation rules themselves: every behavioural claim cites a source file; `SEC-INV` identifiers are
  cited, not restated; out-of-repo behaviour is marked `> **Out of repo:**`.
- The `Verified:` convention — the lint enforces it, it does not replace the obligation to re-verify.
- Pages that deliberately cite sibling-repository sources must keep citing them; the lint skips those paths
  rather than "fixing" them.

## Verification

1. Seed each failure: remove a `Verified:` line, change a version mention, break a relative link, and confirm
   the lint reports exactly those.
2. Confirm the lint is clean on the corrected tree.
3. Confirm it runs in the same job as the existing tests without slowing the critical path noticeably.

## Sources

`README.md`, `docs/20-security/{06-host-privilege-model.md,12-known-limits-and-tradeoffs.md}`,
`docs/70-contributing/02-documentation-style.md`, `.github/instructions/docs.instructions.md`,
`Directory.Build.props`, `Directory.Packages.props`, `scripts/macos/com.csweet.office.node.plist`,
`scripts/tests/Test-OfficeUpgradeProbe.ps1`, `.github/workflows/ci.yml`.

Verified: 2026-09-15.
