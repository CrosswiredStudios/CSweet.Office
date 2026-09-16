---
description: "Documentation style rules for docs/, the root README, and AGENTS.md: cite a source file for every behavioral claim, carry a Verified date, cite SEC-INV identifiers instead of restating rules, mark out-of-repo behavior, and use mermaid rather than ASCII diagrams."
applyTo: "docs/**,README.md,AGENTS.md,.github/copilot-instructions.md,.github/instructions/**,.github/prompts/**,.github/skills/**"
---

# Documentation style

The normative rules live in
[`docs/70-contributing/02-documentation-style.md`](../../docs/70-contributing/02-documentation-style.md). This
file is the short version for when you are editing any markdown in this repository.

## Rules

1. **Every behavioral claim cites a source file.** If you cannot name the file you read it from, do not write
   the claim.
2. **Every page ends with `## Sources` and a `Verified: YYYY-MM-DD.` line.** Refresh the date when you verify
   or change the page.
3. **Security invariants are cited by identifier, never restated.** They are defined once in
   [`docs/20-security/11-security-invariants.md`](../../docs/20-security/11-security-invariants.md) as
   `SEC-INV-01` … `SEC-INV-23`. Never renumber an existing identifier — other files and review comments cite
   them.
4. **Out-of-repo behavior gets a callout and is never inferred.**
   `> **Out of repo:**` followed by what belongs elsewhere and the in-repo evidence that establishes the
   boundary. Headquarters, `CSweet.Office.Contracts`, and `CSweet.Isolation` are all out of repo.
5. **Fixed vocabulary.** Use the terms in [`docs/GLOSSARY.md`](../../docs/GLOSSARY.md): assignment, workload,
   fencing epoch, broker lease, drain, posture, provider, certification. Do not invent synonyms.
6. **Mermaid for diagrams.** No hand-drawn ASCII. Keep diagrams small enough to read without zooming.
7. **`MUST` and `MUST NOT` are reserved for security invariants.** Everywhere else, describe what the code
   does.
8. **Declarative voice, no emoji, no marketing language.** Match the tone of the root `README.md`.
9. **State the audience at the top of each page** (`**Audience:** …`).
10. **Escape `|` inside table cells as `\|`.** A literal pipe silently breaks the row.
11. **Prefer a table to a paragraph** for parameters, defaults, paths, comparisons, and anything with more than
    three parallel items.

## Page skeleton

```markdown
# Title

**Audience:** who this is for.

One paragraph stating what the page covers and why it matters.

...content...

## Sources

`path/one`, `path/two`, `path/three`.

Verified: YYYY-MM-DD.
```

## When you find drift

Fix the page and update the `Verified:` date. If you cannot fix it in this change, record the discrepancy in
the page rather than leaving a claim that is quietly wrong. Known drift belongs on the page, not in a comment
someone will never read.

## Linking

- Use repository-relative paths from the file you are editing.
- Do not link to a heading anchor unless you have verified the heading text.
- Do not link to a page you have not confirmed exists on disk.
