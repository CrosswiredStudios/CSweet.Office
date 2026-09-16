# 10 · System

Orientation pages. Read these first. They establish what Office is, who trusts whom, which components
exist, how the solution is laid out, how data moves end to end, and what actually runs on a host.

| Page | Answers |
|---|---|
| [01-what-is-office.md](01-what-is-office.md) | What is in this repository, what is not, and which repository owns what. |
| [02-trust-model.md](02-trust-model.md) | Who trusts whom, on what evidence, and what each principal is allowed to do. |
| [03-components.md](03-components.md) | Which executables and services exist, under which identity, listening where. |
| [04-solution-map.md](04-solution-map.md) | How the projects depend on each other and why there are two solutions. |
| [05-request-and-data-flow.md](05-request-and-data-flow.md) | Sequence diagrams for enrollment, a runtime workload, and a builder workload. |
| [06-process-network-and-storage-surface.md](06-process-network-and-storage-surface.md) | The complete inventory of processes, sockets, ports, endpoints, and on-disk roots. |

For the vocabulary used across all pages, see [GLOSSARY.md](../GLOSSARY.md).

> **Note:** Pages here describe mechanisms. The normative "must never change" rules live in
> [20-security/11-security-invariants.md](../20-security/11-security-invariants.md).
