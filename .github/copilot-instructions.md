# C-Sweet Office — agent instructions

C-Sweet Office is the independently installed execution plane for C-Sweet agents: an unprivileged Node, a
privileged RuntimeHost, digest-pinned platform helpers, and hardware-isolated guests. It is a shippable
deliverable with its own version and release tags, not a component of the C-Sweet application.

**Read [`AGENTS.md`](../AGENTS.md) before changing anything, and [`docs/README.md`](../docs/README.md) for the
full reference set.**

## Everyday commands

```powershell
# Primary solution (needs a sibling ../CSweet.Office.Contracts checkout)
dotnet build CSweet.Office.slnx -c Release

# Release boundary — always pass the switch explicitly
dotnet build CSweet.Office.Independent.slnx -c Release -p:UseLocalOfficeContracts=false

# Tests
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release -p:UseLocalOfficeContracts=false
```

`UseLocalOfficeContracts` auto-detects a sibling checkout and silently defaults to `true` when one exists.
Pass `-p:UseLocalOfficeContracts=false` whenever you are validating the published package boundary.

## Hard rules

1. **Never weaken a security invariant.** They are numbered `SEC-INV-01` … `SEC-INV-23` in
   [`docs/20-security/11-security-invariants.md`](../docs/20-security/11-security-invariants.md). Cite them by
   identifier; never restate or reinterpret them.
2. **No workload without a signed Headquarters authorization.**
   `RuntimeHostProviderClient.CreateAsync` throws by design — leave it that way (`SEC-INV-07`).
3. **Never reorder authorization validation.** The replay ledger is committed only after signature, digest,
   and time validation succeed (`SEC-INV-08`). Committing earlier turns unauthenticated traffic into a
   denial-of-service primitive.
4. **There is no fallback execution path.** Unavailable, uncertified, expired, or unrecognized providers fail
   closed. Never add a process-level or shared-kernel substitute (`SEC-INV-10`).
5. **Helpers accept exactly eight typed operations over stdio.** Never add a shell, an arbitrary path, a
   command string, or a request-supplied mount option; never make a helper exit non-zero on a typed failure
   (`SEC-INV-20`).
6. **Do not add files under the guest-image fingerprint roots** — `src/CSweet.Office.RuntimeGuest`,
   `src/CSweet.Office.BuilderGuest`, `src/CSweet.Office.ToolchainGuest`, `src/CSweet.Office.Runtime.Protocol`,
   or `build/windows-hyperv`. Any file there, including a README, invalidates the cached guest image and forces
   a full Packer rebuild.
7. **This repository never publishes or signs.** Release signing and certification run only on the hardened
   platform workflows.
8. **Office identity is preserved on upgrade only after drain with zero active assignments** (`SEC-INV-18`).

## Contract changes are cross-repository

`CSweet.Office.Contracts` supplies the control-plane protos, the guest handshake, the authorization envelope,
and the length-delimited framing. Changing any of it means: bump the package semantically, pack it, pin **both**
repositories to the same released version, and verify both with local project references disabled. See
[`docs/50-development/03-contracts-dependency.md`](../docs/50-development/03-contracts-dependency.md).

## Do not "fix" these

They are deliberate. Each has a rationale in
[`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md):

- `OfficeWorker.ValidateAssignment` is duplicated by `RuntimeHostAuthorizationGate.ValidateAndCommit`. Two
  verifications at two privilege levels is the design.
- The assignment replay ledger grows without bound. Pruning it reopens the replay window.
- Helpers return exit code `0` on typed failures; a non-zero exit would discard the typed error.
- Tunnel frame `Sequence = 0` is an empty frame sent before relaying. Without it, three parties deadlock.
- The Hyper-V helper's `Logs()` returns an empty array. It is a stub, and the interface is intentional.
- `ResourceLimits.MaximumDurationSeconds` is not enforced anywhere.
- `OfficeArtifactCache.ImportAsync` throws `NotSupportedException`. Artifacts arrive only through
  assignment-scoped grants.
- Several tests assert on the **text** of scripts rather than their behaviour, so they pass against a
  present-but-broken script. Do not treat a green suite as proof that a script works.

## Where to look

| Need | Page |
|---|---|
| Which project owns what | [`docs/10-system/03-components.md`](../docs/10-system/03-components.md), [`docs/10-system/04-solution-map.md`](../docs/10-system/04-solution-map.md) |
| The trust chain | [`docs/10-system/02-trust-model.md`](../docs/10-system/02-trust-model.md) |
| Every config key and default | [`docs/80-reference/configuration.md`](../docs/80-reference/configuration.md) |
| Every wire message | [`docs/80-reference/wire-protocols.md`](../docs/80-reference/wire-protocols.md) |
| Which test pins what | [`docs/80-reference/tests.md`](../docs/80-reference/tests.md) |
| What to update when you change X | [`docs/70-contributing/03-change-impact-matrix.md`](../docs/70-contributing/03-change-impact-matrix.md) |
| Documentation rules | [`docs/70-contributing/02-documentation-style.md`](../docs/70-contributing/02-documentation-style.md) |

## Documentation obligation

Every behavioral claim in `docs/` cites a source file and carries a `Verified:` date. When you change
behavior, update the affected pages and refresh their `Verified:` date. If you cannot, record the discrepancy
explicitly rather than leaving a claim that is quietly wrong.
