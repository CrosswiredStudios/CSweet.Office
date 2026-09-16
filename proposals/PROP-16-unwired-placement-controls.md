# PROP-16 — Resolve the unwired placement controls

**Status:** proposal — not implemented. **Area:** quality (clarity of the security surface). **Effort:**
medium if relocated, small if frozen in place.

**Tooling cost:** none if the controls stay; a cross-repository release if they move.

**Invariant interactions:** `SEC-INV-10` is the invariant these types implement. Moving or marking them must
not change the rule they express, and the review checklist keeps citing the tests that pin it.

## Problem

Two implemented, documented, tested security controls have **no production call site in this repository**:

- `FailClosedIsolationProviderSelector` — raises every request to `CertifiedHardwareVirtualMachine`, orders
  candidates deterministically, and refuses to return a provider without a live probe carrying an active,
  identity-matching certification.
- `CertifiedGuestImageRegistry` — resolves image references from the selected provider's certification and
  rejects a stale certification suite version.

[`docs/20-security/08-provider-certification.md`](../docs/20-security/08-provider-certification.md) says so
explicitly for the registry ("This registry has no call site in this repository. It is the Headquarters-side
placement library, covered by `CertifiedGuestImageRegistryTests`"), and the selector has the same property:
only tests and documentation reference it. What *is* wired in this repository is the rest of the stack —
`ExternalPlatformIsolationBackend` performs the create-time validations, and `RuntimeHostInventory` feeds the
probe result to Headquarters.

That leaves a naming and ownership ambiguity in the most security-relevant part of the solution:

- The review checklist's `SEC-INV-10` row cites `IsolationProviderSelectorTests` as if it pinned runtime
  behaviour, when in this repository there is no runtime caller to pin.
- A contributor reading `Runtime.Core` sees a selector that nothing selects with, and may reasonably
  conclude it is dead code or try to wire it into `RuntimeHost` where placement does not belong.
- A reviewer cannot tell from the repository whether Headquarters consumes these types through the Contracts
  package, through a copied implementation, or not at all.

## User story

**Scenario.** A new maintainer is asked whether Office enforces the certified-provider floor at runtime. They
grep for the selector's name, find no call sites, find tests that exercise it directly, and cannot tell
whether the enforcement is real, misplaced, or leftover. Answering takes an afternoon of cross-repository
investigation.

> As a maintainer, I want the placement library's home and consumer to be unambiguous, so that its ownership,
> its enforcement point, and its tests have an obvious place in the system.

## Recommended fix

Establish the consumption fact first — this is a question for the Headquarters side, and the answer decides
between two clean outcomes:

### Outcome 1 — Headquarters consumes them (relocate)

Move `FailClosedIsolationProviderSelector` and `CertifiedGuestImageRegistry` (and the tests) out of
`Runtime.Core` into the package Headquarters actually references (`CSweet.Office.Contracts`, or a new small
placement package if the abstraction types they depend on do not belong there). Bump, pack, pin both
repositories, and verify both with local project references disabled, per
[`AGENTS.md`](../AGENTS.md). `Runtime.Core` keeps what it actually uses: the backend, the payload manifest,
the ISO writer, and the in-memory test doubles.

### Outcome 2 — They stay here as a published library (freeze and mark)

If relocation is too costly, make the boundary impossible to miss:

- Add an XML-doc remark on both types stating that no code in this repository calls them and naming their
  consumer.
- Add a short section to [`docs/80-reference/projects/runtime-core.md`](../docs/80-reference/projects/runtime-core.md)
  stating that they are the placement library consumed outside the Office runtime.
- Correct the review checklist's `SEC-INV-10` row so it distinguishes what is enforced in-repo
  (`ExternalPlatformIsolationBackend` create-time checks, probe identity, certification activity) from what is
  enforced in the placement library.
- Optionally, add a test that fails if a production type in another project starts calling the selector
  without a corresponding documentation change — acknowledging that the same assembly makes a hard rule
  impossible; the marker plus docs is the practical guard.

Do not delete them: they are tested statements of `SEC-INV-10` and the image-resolution contract, and the
tests are cited by the invariants page.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `src/CSweet.Office.Runtime.Core/{FailClosedIsolationProviderSelector.cs,CertifiedGuestImageRegistry.cs}` | Relocate (outcome 1) or annotate (outcome 2) | |
| `tests/CSweet.Office.Tests/{IsolationProviderSelectorTests.cs,CertifiedGuestImageRegistryTests.cs}` | Move with them, or leave with a pointer | The invariants page cites these tests |
| `CSweet.Office.Contracts` | Outcome 1: new home, package bump, dual-repository pin | Cross-repository release flow |
| Docs | [`docs/20-security/08-provider-certification.md`](../docs/20-security/08-provider-certification.md), [`docs/80-reference/projects/runtime-core.md`](../docs/80-reference/projects/runtime-core.md), [`docs/70-contributing/04-review-checklist.md`](../docs/70-contributing/04-review-checklist.md), [`docs/70-contributing/03-change-impact-matrix.md`](../docs/70-contributing/03-change-impact-matrix.md) if files move | Refresh `Verified:` dates |

## What must not change

- `SEC-INV-10`'s substance: the floor is raised, never lowered; nothing is selected without a live probe and
  an active, identity-matching certification.
- The create-time validations in `ExternalPlatformIsolationBackend`, which are the in-repo enforcement.
- The tests that pin the behaviour, wherever they live.

## Verification

1. Outcome 1: both repositories build and test with `-p:UseLocalOfficeContracts=false`.
2. Outcome 2: the XML-doc remark and the docs statements exist; the corrected review-checklist row names the
   in-repo enforcement points.
3. Either way, grep confirms there is exactly one authoritative implementation and it has one declared
   consumer.

## Sources

`src/CSweet.Office.Runtime.Core/{FailClosedIsolationProviderSelector.cs,CertifiedGuestImageRegistry.cs,ExternalPlatformIsolationBackend.cs}`,
`src/CSweet.Office.Node/ProviderInventory.cs`,
`docs/20-security/{08-provider-certification.md,11-security-invariants.md}`,
`docs/80-reference/projects/runtime-core.md`, `docs/70-contributing/{03-change-impact-matrix.md,04-review-checklist.md}`,
`tests/CSweet.Office.Tests/{IsolationProviderSelectorTests.cs,CertifiedGuestImageRegistryTests.cs}`.

Verified: 2026-09-15.
