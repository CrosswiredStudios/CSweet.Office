---
description: "Review a diff or a pull request against the twenty-three security invariants, citing identifiers and naming the test that pins each one."
mode: agent
---

# Review against the security invariants

Use this to review a change that touches `Runtime.LocalRpc`, `RuntimeHost`, `Runtime.Protocol`, `Runtime.Core`,
the provider backends, the helpers, the guests, or the installers.

## Method

1. Read the diff and list which files it touches.
2. For each file, find the matching entry in the invariant table below and check the diff against it. Cite the
   identifier in your finding rather than restating the rule.
3. For each invariant the change could affect, name the test that pins it — or state that no test exists, which
   the invariants page records explicitly for several entries.
4. Report findings as `SEC-INV-nn — file — what the change does — why it matters`.

## Invariants

| Id | Check |
|---|---|
| `SEC-INV-01` | The Headquarters trust pin is write-once, and the `Guid.Empty` → real-office upgrade still exists. |
| `SEC-INV-02` | Certificate replacement still validates expiry, thumbprint, and key binding before writing. |
| `SEC-INV-03` | Recovery still sends no expired client certificate and no reusable receipt. |
| `SEC-INV-04` | Bootstrap detection and `AllowTlsResume = false` are unchanged. |
| `SEC-INV-05` | Retired-certificate retention stays at least two minutes. |
| `SEC-INV-06` | Control messages are still bound to both office id and session epoch. |
| `SEC-INV-07` | No create path bypasses the authorization gate; `CreateAsync` still throws. |
| `SEC-INV-08` | Validation order is unchanged; the ledger still commits last. |
| `SEC-INV-09` | The epoch rule is still `>=`, and the ledger is not pruned. |
| `SEC-INV-10` | The assurance floor is raised, never lowered; no provider passes without an active certification. |
| `SEC-INV-11` | The numeric clamps are unchanged. |
| `SEC-INV-12` | Pipe DACL and Unix socket mode are unchanged. |
| `SEC-INV-13` | Failed frames still get no response; signatures still precede nonces. |
| `SEC-INV-14` | Responses are still signed and request-id-bound. |
| `SEC-INV-15` | Handle authorization is still bound to provider, instance, and kind. |
| `SEC-INV-16` | Package and image ACLs are still read-only for the services. |
| `SEC-INV-17` | Installation still refuses on a domain controller. |
| `SEC-INV-18` | No identity migration; `reconnect` still wipes mutable trust. |
| `SEC-INV-19` | The maintenance service is still bound to the administrator-owned install tree. |
| `SEC-INV-20` | The helper surface is unchanged and the digest is still verified per invocation. |
| `SEC-INV-21` | The guest environment is still cleared and rebuilt from the allow-list. |
| `SEC-INV-22` | Mount flags and extraction limits are unchanged. |
| `SEC-INV-23` | The guest handshake, one-shot challenge, and lease cancellation are unchanged. |

## Also check

- Does the change add a file under `src/CSweet.Office.RuntimeGuest`, `BuilderGuest`, `ToolchainGuest`,
  `Runtime.Protocol`, or `build/windows-hyperv`? That forces a guest rebuild.
- Does it add a dependency without a `PackageVersion` in `Directory.Packages.props`?
- Does it add `CopyToOutputDirectory` to a non-code file?
- Are the affected `docs/` pages updated, and are their `Verified:` dates refreshed?
- Does a "cleanup" remove something listed in
  [`docs/20-security/12-known-limits-and-tradeoffs.md`](../../docs/20-security/12-known-limits-and-tradeoffs.md)?

The authoritative text is
[`docs/20-security/11-security-invariants.md`](../../docs/20-security/11-security-invariants.md).
