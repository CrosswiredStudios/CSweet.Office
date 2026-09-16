# Improvement proposals

Engineering proposals for changes that would improve the quality, speed, or security of C-Sweet Office.
Each proposal is a standalone page: problem analysis, a user story, the recommended fix, and the changes the
fix would require.

**These are proposals. None of them is implemented.** Nothing in this directory describes current behaviour.
Where a proposal interacts with a security invariant or a deliberate behaviour recorded in
[`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md),
the interaction is named explicitly in the page.

| Id | Title | Area | Effort | Tooling cost |
|---|---|---|---|---|
| [PROP-01](PROP-01-inspect-poll-cost.md) | Reduce the cost of the workload inspect loop | Speed | M | Payload rebuild |
| [PROP-02](PROP-02-redundant-artifact-hashing.md) | Stop hashing the same payload on every launch | Speed | S–M | Payload rebuild |
| [PROP-03](PROP-03-probe-cost-across-reconnects.md) | Bound probe cost across reconnects | Speed | S–M | Payload rebuild |
| [PROP-04](PROP-04-writethrough-on-bulk-writes.md) | Remove `WriteThrough` from bulk artifact and media writes | Speed | S | Payload rebuild |
| [PROP-05](PROP-05-tunnel-relay-tuning.md) | Tune the guest tunnel relay | Speed | M | Payload rebuild; guest rebuild only for the optional second stage |
| [PROP-06](PROP-06-builder-reap-policy.md) | Make workload reaping lease-driven, not kind-driven | Security | M | Payload rebuild and certification smoke path |
| [PROP-07](PROP-07-office-key-storage-hardening.md) | Harden Office identity key storage | Security | L | Payload rebuild |
| [PROP-08](PROP-08-runtime-host-key-acl.md) | Narrow interactive read access to `runtime-host.key` | Security | M–L | Installer scripts (payload-staged) |
| [PROP-09](PROP-09-maximum-duration-enforcement.md) | Decide the fate of `ResourceLimits.MaximumDurationSeconds` | Security | S (decision) / M (enforce) | Guest rebuild and certification if enforced in the guest; cross-repository if removed from the contract |
| [PROP-10](PROP-10-confidential-computing-attestation.md) | Record a decision on confidential-computing attestation | Security | XL (research) | Cross-repository |
| [PROP-11](PROP-11-windows-ci-coverage.md) | Exercise Windows code paths in CI | Quality | M | None |
| [PROP-12](PROP-12-editorconfig-analyzers.md) | Add `.editorconfig` and analyzer enforcement | Quality | S | One-time guest-image cache invalidation if `Directory.Build.props` is touched |
| [PROP-13](PROP-13-script-behaviour-tests.md) | Replace script-text assertions with behavioural tests | Quality | L | None for tests; avoid touching `New-CSweetHyperVTestGuest.ps1` (fingerprinted) |
| [PROP-14](PROP-14-documentation-drift-guardrails.md) | Add documentation drift guardrails | Quality | S–M | None |
| [PROP-15](PROP-15-guest-fingerprint-ergonomics.md) | Make guest-image fingerprint changes visible | Quality | S | None — `Initialize-CSweetWindowsIsolationTest.ps1` is not itself fingerprinted |
| [PROP-16](PROP-16-unwired-placement-controls.md) | Resolve the unwired placement controls | Quality | M | Cross-repository if relocated |

## Usage

A proposal is a discussion input, not a work order. Before implementing one:

1. Check its "Problem" section against the cited files — if the code has moved on, the proposal may be stale.
2. Read the "What must not change" section and the referenced invariants.
3. Apply the change-impact matrix in
   [`docs/70-contributing/03-change-impact-matrix.md`](../docs/70-contributing/03-change-impact-matrix.md) to
   the files you actually touch, not to the files the proposal names.

Verified: 2026-09-15.
