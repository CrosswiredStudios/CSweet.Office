# PROP-10 — Record a decision on confidential-computing attestation

**Status:** proposal — not implemented. **Area:** security direction. **Effort:** XL as an implementation;
the deliverable here is a decision record.

**Tooling cost:** cross-repository. Any provider that gains an attestation capability changes its descriptor,
and a descriptor change is a certification identity change (provider id, version, OS, architecture), so every
existing certification becomes inapplicable until re-issued.

**Invariant interactions:** none. `SEC-INV-10` (assurance floor, active certification) and `SEC-INV-23` (guest
handshake) are the surfaces a future implementation would touch.

## Problem

Office explicitly does not claim confidential computing or remote attestation:

- [`docs/20-security/10-threat-model.md`](../docs/20-security/10-threat-model.md) lists "Confidential computing
  attestation" as an explicit non-goal, and the capability table in
  [`docs/20-security/08-provider-certification.md`](../docs/20-security/08-provider-certification.md) shows the
  asymmetry: Hyper-V declares neither measured nor verified boot; Firecracker and Apple Virtualization declare
  measured/verified boot but provide no remote attestation to Headquarters through this repository.
- The trust model states the host operating system and host administrator are trusted
  ([`docs/10-system/02-trust-model.md`](../docs/10-system/02-trust-model.md)).

That is a defensible position. The problem is that it is only discoverable by reading two pages carefully. In
sales conversations, security reviews, and provider investment decisions, "does the platform prove what is
running in the guest?" gets a different answer depending on who is asked, and the repository has no dated
decision to point at.

## User story

**Scenario.** A prospective customer with a regulated workload asks whether their agent's execution is
attested: can the host operator prove to them that the guest ran the certified image, unchanged? The field
engineer says "Firecracker supports measured boot"; the security reviewer says "the threat model excludes
attestation"; the product owner hears both and plans a commitment neither statement supports.

> As a platform owner, I want one recorded decision about attestation, so that commitments, provider
> investments, and security documentation stay consistent.

## Recommended fix

Write a decision record (one page, dated, in `docs/` if adopted) that answers four questions:

1. **Is host-level attestation in scope for the next N releases?** If no, state what would have to change for
   it to come into scope.
2. **If yes, what is the minimal honest first step?** Candidates, in increasing cost:
   - Expose measured/verified boot through the existing capability flags and provider inventory so
     Headquarters can place only where measurements are meaningful. This is mostly documentation and
     descriptor hygiene and changes no isolation behaviour.
   - Add a provider probe field carrying an attestation-evidence blob, and a guest-handshake extension that
     binds a Headquarters nonce into the evidence. The handshake already has the shape for this: a one-shot
     challenge answered with a signature (`SEC-INV-23`), and the guest image digest is already bound into the
     expected identity.
   - Add a Headquarters verification service with pinned vendor roots (AMD/Intel for SEV-SNP/TDX, vTPM or
     measured-boot roots where applicable) and a policy that requires a valid report before placement. This is
     the cross-repository bulk of the work.
3. **Which hosts could even answer?** SEV-SNP and TDX require host enablement and are not universally present;
   Hyper-V guests would need a different mechanism (for example a vTPM-backed measured chain and a host
   attestation service). State which provider/OS combinations are realistic and which are aspirational.
4. **What does it mean for the baseline/hardened postures?** An attested placement is a stronger posture than
   `hardened`; it should be a named posture rather than an implicit property, and the installer's posture
   reporting already provides the hook.

Whatever the answer, the outcome must also update the threat model's non-goal wording so the two pages agree.

## Changes required

| Area | Change | Notes |
|---|---|---|
| Documentation | A dated decision record; update [`docs/20-security/10-threat-model.md`](../docs/20-security/10-threat-model.md) and [`docs/20-security/08-provider-certification.md`](../docs/20-security/08-provider-certification.md) to point at it | Start here; the rest of the table applies only if the decision is "yes" |
| `CSweet.Office.Contracts` | Guest handshake and/or provider inventory fields for attestation evidence | Cross-repository release |
| Provider backend and probe | A `probe` evidence field and provider-specific collection | Helper payload rebuild per platform |
| Headquarters (out of repo) | Verification service, pinned roots, placement policy | The largest share of the work |
| Tests | If implemented: evidence binding tests, rejection of a stale or foreign report | |

## What must not change

- The assurance floor and the fail-closed selector (`SEC-INV-10`) — attestation may raise the floor, never
  become a substitute for certification.
- The guest handshake's HMAC proof, one-shot challenge, and lease cancellation (`SEC-INV-23`); an attestation
  extension is an addition, not a replacement.
- The trust model's honest statement of what is not defended until a mechanism actually exists.

## Verification

A decision record has no runtime verification. If adopted: a test host with SEV-SNP (or the chosen
mechanism), a Headquarters stub that rejects a mismatched report, and evidence that placement refuses
un-attested providers when policy demands attestation.

## Sources

`docs/20-security/{08-provider-certification.md,10-threat-model.md,11-security-invariants.md}`,
`docs/10-system/02-trust-model.md`, `docs/30-workloads/06-guest-images.md`,
`src/CSweet.Office.Runtime.Abstractions/IsolationProviderCatalog.cs`,
`src/CSweet.Office.Runtime.Core/FailClosedIsolationProviderSelector.cs`.

Verified: 2026-09-15.
