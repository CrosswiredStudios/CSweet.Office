# 80 · Reference

**Audience:** anyone who needs a specific fact — a default value, an error code, a file mode, a script
parameter, a test name — rather than an explanation.

This section is exhaustive by design. It is written to be looked up, not read start to finish. Where the
other sections teach a lifecycle or an argument, this section enumerates the surface: every configuration
key, every protocol field, every status string, every installed path, every script, and every test.

The pages are written against the code. Where a page and the code disagree, the code and the tests win; see
[../README.md](../README.md) for the documentation conventions and
[../70-contributing/02-documentation-style.md](../70-contributing/02-documentation-style.md) for the rules
these pages follow.

## Core reference pages

| Page | Covers | Read it when |
|---|---|---|
| [configuration.md](configuration.md) | Every options class, its `SectionName` constant, its properties with defaults and validation, the real `appsettings.json` the Windows installer writes, the systemd and launchd overrides, and the machine-scope `CSWEET_*` variables. | You are changing a default, adding a setting, or debugging why a service started with an unexpected value. |
| [wire-protocols.md](wire-protocols.md) | The local RPC framing and all 16 `RuntimeHostEnvelope` arms, the control-plane JSON endpoints and the three `OfficeGateway` RPCs, and the guest broker framing, boot configuration, handshake, and commands. | You are changing a message, a field number, or a stream shape. |
| [status-and-error-codes.md](status-and-error-codes.md) | Every status string, failure code, rejection code, helper error code, guest reason code, and exit code, grouped by producer. | An operator reported a code, or you are adding one. |
| [file-layout.md](file-layout.md) | Every install path and state path per platform with owner and mode or ACL, the payload layout, the guest disk layout, and the artifact cache and ISO naming rules. | You are writing an ACL, a systemd unit, or a cleanup script. |
| [scripts.md](scripts.md) | All 35 scripts under `scripts/`, grouped by folder, with parameters, prerequisites, outputs, and exit codes. | You are running, changing, or reviewing a script. |
| [tests.md](tests.md) | Every test class in `tests/CSweet.Office.Tests`, what it pins, and the invariant-to-test mapping. | You are checking whether a behaviour is covered before changing it. |
| [cross-repo-contracts.md](cross-repo-contracts.md) | What `CSweet.Office.Contracts` supplies, the pin, the transitive `CSweet.Isolation.Security` dependency, and the boundary-verification procedure. | You are changing a contract type or validating the independent solution. |

## Per-project pages

| Page | Covers |
|---|---|
| [projects/README.md](projects/README.md) | Index of all 19 projects with output kind, target framework, project references, purpose, the dependency graph, and solution membership. |
| `projects/<project>.md` | One page per `src/` folder: project facts, load-bearing types, entry-point composition, non-obvious behaviour, test coverage, and related documentation. |

The project pages are the canonical type-level reference. The seven core pages above are organised by
subject, so a fact about one project can appear on several of them.

## How to use this section

- Every row has a source. Each page ends with a `## Sources` list of the files its claims were read from,
  and a `Verified:` date.
- Behaviour that lives in another repository is marked with a `> **Out of repo:**` callout. Headquarters
  (C-Sweet), the `CSweet.Office.Contracts` package, and the `CSweet.Isolation` tooling are not in this
  repository, and no page here infers their behaviour.
- Security invariants are cited by identifier (`SEC-INV-nn`) and defined only in
  [../20-security/11-security-invariants.md](../20-security/11-security-invariants.md).
- Tables escape a literal pipe as `\|`.

## Sources

`docs/README.md`, `docs/20-security/11-security-invariants.md`, `docs/80-reference/projects/README.md`,
`docs/70-contributing/02-documentation-style.md`.

Verified: 2026-09-15.
