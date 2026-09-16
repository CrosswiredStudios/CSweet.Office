# PROP-15 — Make guest-image fingerprint changes visible

**Status:** proposal — not implemented. **Area:** quality (developer experience). **Effort:** small.

**Tooling cost:** none. `Initialize-CSweetWindowsIsolationTest.ps1` is not itself in the fingerprint set
(the set includes `New-CSweetHyperVTestGuest.ps1`, the root props files, and the listed roots — not this
script), so editing it must not change the fingerprint. Confirm that with a before/after comparison in the
pull request, because the opposite would be ironic.

**Invariant interactions:** none. The fingerprint algorithm and the cache key stay exactly as they are; this
proposal only adds visibility.

## Problem

`Get-GuestBuildFingerprint` hashes every non-`bin`/`obj` file under the five in-repo roots, the sibling
`CSweet.Isolation/tools/LinuxImage` tree, `scripts/windows/New-CSweetHyperVTestGuest.ps1`, and the three root
build files, producing **one** aggregate hash. The image is named
`csweet-agent-guest-<fingerprint>.vhdx`, with a `.ready` marker containing the fingerprint; when the computed
fingerprint differs, the next Windows development run silently builds a new image.

The cost of an accidental invalidation is a full Packer rebuild, and the developer who triggered it usually
does not know: the build output does not say *which* file changed. Two recurring causes are documented and
easy to hit:

- adding any file under a fingerprint root, including a README (the reason several instruction files carry a
  "stop before you add a file here" warning);
- a `VersionPrefix` bump in `Directory.Build.props`, which is fingerprinted, so a version-only change costs a
  guest rebuild.

The information needed to explain the change already exists inside the function — it builds a per-file record
list before collapsing it — but nothing retains or displays it.

## User story

**Scenario.** A developer adds a short README next to the builder guest code to explain a subsystem to the next
person. The next test run triggers a twenty-minute guest rebuild. Nothing in the output mentions the README,
so the developer assumes the tooling is flaky and re-runs it twice before giving up and asking a colleague.

> As a developer, I want the tooling to tell me which files changed the fingerprint, so that I can either undo
> the mistake or understand the rebuild I just triggered.

## Recommended fix

1. **Persist the per-file records.** Alongside the built image, write a manifest of the exact records the
   fingerprint is computed from (relative path and SHA-256), for example
   `<guestImageRoot>\csweet-agent-guest-<fingerprint>.manifest.txt`.
2. **Diff on mismatch.** When the computed fingerprint does not match the cached marker, find the most recent
   manifest, diff it against the current records, and print the added, removed, and changed files (sorted,
   capped at a sensible count) before starting the rebuild. When no previous manifest exists, print a single
   line saying the comparison is unavailable for this build.
3. **Keep the algorithm identical.** The aggregate hash, the image name, and the `.ready` marker semantics do
   not change; the manifest is diagnostic output, never an input to the hash.
4. **Mention the mechanism in the docs** so the warning becomes "the tool will tell you", not "the tool will
   punish you".

Optionally, the same diff can be printed by a small `-WhatIf`-style switch so a developer can check before
running the whole loop.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1` | Emit the manifest; diff and print on fingerprint mismatch | Do not change the fingerprint computation itself |
| `docs/50-development/09-guest-image-changes.md` | Describe the diff output and the manifest file | Refresh `Verified:` |
| `.github/instructions/guest-and-build-images.instructions.md` | One sentence: the tool prints which files invalidated the cache | Keeps the warning accurate |

## What must not change

- The fingerprint inputs and algorithm; this is diagnostics only.
- The rule that any file added under a fingerprint root invalidates the cache — visibility, not amnesty.
- The `New-CSweetHyperVTestGuest.ps1` invocation contract and the `.ready` marker semantics.

## Verification

1. Run the script twice with no changes: the fingerprint is identical and no diff is printed; the manifest is
   created once.
2. Add a file under a fingerprint root and run again: the diff names exactly that file.
3. Touch `Directory.Build.props` version: the diff names that file, confirming the surprising case is now
   self-explanatory.

## Sources

`scripts/windows/Initialize-CSweetWindowsIsolationTest.ps1` (`Get-GuestBuildFingerprint` and the cache-key
logic), `docs/50-development/09-guest-image-changes.md`,
`docs/70-contributing/03-change-impact-matrix.md` (the `VersionPrefix` row),
`.github/instructions/guest-and-build-images.instructions.md`, `AGENTS.md` hard rule 6.

Verified: 2026-09-15.
