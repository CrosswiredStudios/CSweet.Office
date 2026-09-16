# 70 · Contributing

**Audience:** everyone opening a change against this repository.

This section is the contributor contract beyond the four normative rules in
[`../AGENTS.md`](../AGENTS.md): repository conventions, the documentation style rules, the change-impact
matrix, and the review checklist. `AGENTS.md` applies to every change; these pages explain how to satisfy it.

| Page | Covers |
|---|---|
| [01-repository-conventions.md](01-repository-conventions.md) | The four normative rules, how a change reaches `main`, and the files that must not be added. |
| [02-documentation-style.md](02-documentation-style.md) | The full normative style rules for `docs/`. |
| [03-change-impact-matrix.md](03-change-impact-matrix.md) | "If you change X, you must also update or rebuild Y", row by row. |
| [04-review-checklist.md](04-review-checklist.md) | The invariant-keyed review table, plus the general review items. |

Two things to read before your first change:

- [../20-security/11-security-invariants.md](../20-security/11-security-invariants.md) — the numbered
  `SEC-INV-nn` rules. The review checklist is organised around them.
- `docs/10-system/04-solution-map.md` — the two solutions and the `UseLocalOfficeContracts` switch, which is
  the most common source of build confusion in this repository.

## Sources

`AGENTS.md`, `docs/README.md`, `.github/workflows/ci.yml`, `docs/20-security/11-security-invariants.md`.

Verified: 2026-09-15.
