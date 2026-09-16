# C-Sweet Office documentation

This directory is the reference documentation for the C-Sweet Office execution plane. It covers the
whole system: what Office is, how it is trusted, how work flows through it, how it is built, released,
installed, and operated.

The audience is deliberately broad — LLM coding agents, human contributors, security reviewers, and
operators. Every page states which audience it serves and which source files it was verified against.

If you only read one thing, read [10-system/01-what-is-office.md](10-system/01-what-is-office.md).

## Reading paths

| Role | Read in this order |
|---|---|
| **LLM coding agent** | [`../AGENTS.md`](../AGENTS.md) → [`../.github/copilot-instructions.md`](../.github/copilot-instructions.md) → [50-development/02-build-and-test.md](50-development/02-build-and-test.md) → [20-security/11-security-invariants.md](20-security/11-security-invariants.md) → the `docs/50-development/` page for your task |
| **New contributor** | [10-system/01-what-is-office.md](10-system/01-what-is-office.md) → [10-system/03-components.md](10-system/03-components.md) → [10-system/04-solution-map.md](10-system/04-solution-map.md) → [50-development/01-getting-started.md](50-development/01-getting-started.md) → [50-development/02-build-and-test.md](50-development/02-build-and-test.md) → [50-development/04-windows-dev-loop.md](50-development/04-windows-dev-loop.md) |
| **Security reviewer** | [10-system/02-trust-model.md](10-system/02-trust-model.md) → [20-security/](20-security/README.md) in section order → [20-security/11-security-invariants.md](20-security/11-security-invariants.md) → [20-security/12-known-limits-and-tradeoffs.md](20-security/12-known-limits-and-tradeoffs.md) |
| **Operator or administrator** | [10-system/01-what-is-office.md](10-system/01-what-is-office.md) → [40-operations/01-installation-windows.md](40-operations/01-installation-windows.md) (or [Linux](40-operations/02-installation-linux.md) / [macOS](40-operations/03-installation-macos.md)) → [40-operations/04-upgrade-and-drain.md](40-operations/04-upgrade-and-drain.md) → [40-operations/07-diagnostics-and-troubleshooting.md](40-operations/07-diagnostics-and-troubleshooting.md) |
| **Release engineer** | [60-release/01-versioning-and-compatibility.md](60-release/01-versioning-and-compatibility.md) → [60-release/02-payload-and-manifest.md](60-release/02-payload-and-manifest.md) → [60-release/03-certification.md](60-release/03-certification.md) → [60-release/04-release-pipeline.md](60-release/04-release-pipeline.md) |

## Sections

| Section | Covers |
|---|---|
| [10-system](10-system/README.md) | What Office is, the trust chain, the component map, the solution layout, end-to-end data flow, and every process, socket, and directory on a host. |
| [20-security](20-security/README.md) | Office identity, certificates, Headquarters trust pinning, signed workload authorization, the local RPC boundary, host privilege models, guest isolation, provider certification, the helper protocol, the threat model, and the normative invariant list. |
| [30-workloads](30-workloads/README.md) | Assignments and leases, the runtime, builder, and toolchain workload lifecycles, the guest broker protocol, artifacts and media, guest images, and each provider backend. |
| [40-operations](40-operations/README.md) | Installing, upgrading, draining, repairing, diagnosing, and removing an Office on Windows, Linux, and macOS; security postures. |
| [50-development](50-development/README.md) | Getting started, build and test, the contracts dependency, platform development loops, the testing guide, adding a provider, and code conventions. |
| [60-release](60-release/README.md) | Versioning, the payload and runtime manifest, certification, the release pipeline, signing and provenance, and the release-notes process. |
| [70-contributing](70-contributing/README.md) | Repository conventions, the documentation style rules, the change-impact matrix, and the review checklist. |
| [80-reference](80-reference/README.md) | Configuration, wire protocols, status and error codes, file layout, scripts, tests, the cross-repo contracts surface, and a page per project. |

[GLOSSARY.md](GLOSSARY.md) defines the vocabulary used throughout.

## Documentation conventions

These conventions are normative for this directory. [70-contributing/02-documentation-style.md](70-contributing/02-documentation-style.md)
holds the full rules; the essentials are:

- **Every behavioral claim cites a source file.** Pages end with a `## Sources` section listing the
  files the content was read from.
- **Every page records when it was verified.** The `## Sources` section carries a `Verified:` date.
- **Security invariants have stable identifiers.** `SEC-INV-nn` identifiers are defined once in
  [20-security/11-security-invariants.md](20-security/11-security-invariants.md) and referenced by
  identifier everywhere else — in this directory, in `AGENTS.md`, and in the agent customization files.
  Never restate an invariant; cite it.
- **Out-of-repo behavior is marked, never inferred.** Headquarters (C-Sweet), the
  `CSweet.Office.Contracts` package, and the `CSweet.Isolation` tooling live in other repositories.
  Pages use a `> **Out of repo:**` callout to state precisely which side of the boundary owns a
  behavior, and cite the in-repo evidence that establishes the boundary.
- **Mermaid for diagrams.** Do not hand-draw ASCII diagrams. Keep diagrams small enough to read without
  zooming.
- **Declarative voice.** Short sentences, no emoji, no marketing language. Match the tone of the root
  `README.md` and `releases/*.md`.
- **`MUST` / `MUST NOT` are reserved for security invariants.** Everywhere else, describe what the code
  does.

## The code is the specification

This repository has no separate specification document, and these pages are not one. Where these pages
and the code disagree, the code and the tests win. The test suite is the closest thing to a written
specification for the security-critical paths; [80-reference/tests.md](80-reference/tests.md) lists
which invariant each test class pins.

If you find drift, fix the page and update its `Verified:` date. If you cannot fix it, note the
discrepancy in the page rather than leaving a claim that is quietly wrong.
