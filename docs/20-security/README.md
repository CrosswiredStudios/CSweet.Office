# 20 · Security

The security model of the execution plane, page by page. Mechanisms are described in
[10-system/02-trust-model.md](../10-system/02-trust-model.md); these pages are the detail.

| Page | Covers |
|---|---|
| [01-identity-and-enrollment.md](01-identity-and-enrollment.md) | Office state, the enrollment exchange, the bootstrap certificate, session epochs. |
| [02-certificate-lifecycle.md](02-certificate-lifecycle.md) | Live renewal, the one-use recovery challenge, and the invalidation-safe install path. |
| [03-headquarters-trust-pinning.md](03-headquarters-trust-pinning.md) | How assignment trust is pinned, verified, and what invalidates it. |
| [04-workload-authorization.md](04-workload-authorization.md) | The signed envelope, double verification, the replay ledger, and handle authorization. |
| [05-local-rpc-boundary.md](05-local-rpc-boundary.md) | HMAC envelope, nonce replay, framing, transport ACLs, and the no-oracle rule. |
| [06-host-privilege-model.md](06-host-privilege-model.md) | Service accounts, ACLs, Hyper-V access, and the immutable package on all three platforms. |
| [07-guest-isolation.md](07-guest-isolation.md) | Artifact materialization, the environment allow-list, the workload user, and the broker handshake. |
| [08-provider-certification.md](08-provider-certification.md) | Certification evidence, expiry, the fail-closed selector, and manifest binding. |
| [09-helper-protocol.md](09-helper-protocol.md) | The narrow helper surface: eight operations, typed JSON, per-invocation digest checks. |
| [10-threat-model.md](10-threat-model.md) | Assets, adversaries, boundaries, and explicit non-goals. |
| [11-security-invariants.md](11-security-invariants.md) | **Normative.** The numbered `SEC-INV-nn` rules that must not be broken. |
| [12-known-limits-and-tradeoffs.md](12-known-limits-and-tradeoffs.md) | Accepted weaknesses, with rationale and blast radius. |

## How to use these pages

- If you are **changing code** in `Runtime.LocalRpc`, `RuntimeHost`, `Runtime.Protocol`, `Runtime.Core`, the
  provider backends, the helpers, or the guests: read
  [11-security-invariants.md](11-security-invariants.md) first, then the mechanism page for your area, then
  [12-known-limits-and-tradeoffs.md](12-known-limits-and-tradeoffs.md) so you do not "fix" something that is
  deliberate.
- If you are **reviewing** a change: the review checklist in
  [70-contributing/04-review-checklist.md](../70-contributing/04-review-checklist.md) enumerates what to look
  for, keyed to invariant identifiers.
- If you are **auditing**: read the section in order. Each page ends with the source files it was verified
  against.
