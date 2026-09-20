# C-Sweet Office contributor instructions

- This repository is an independently versioned installable deliverable. Do not couple its tags to C-Sweet headquarters tags.
- If `CSweet.Office.Contracts` changes, bump that package using semantic versioning, pack it, update this repository and C-Sweet to the same released pin, and verify both with local project references disabled.
- Production release signing and certification require the hardened platform workflows. The user-authorized `hosted-release.yml` exception publishes development bundles from GitHub-hosted runners without signing secrets; each destination host certifies and development-signs its guest before installation. Do not represent hosted bundles as production-signed or CI-certified.
- Preserve Office identity on upgrades only after the office is drained and has zero active assignments. A first install removes legacy Execution Node services but always enrolls a fresh identity.

## What this repository is

The independently installed execution plane: an unprivileged control client (`CSweet.Office.Node`), a
privileged virtualization service (`CSweet.Office.RuntimeHost`), a Windows maintenance service, digest-pinned
platform helpers, and hardware-isolated guests. It is a shippable deliverable with its own `vX.Y.Z` tags.

Reference documentation lives in [`docs/`](docs/README.md). Start at
[docs/10-system/01-what-is-office.md](docs/10-system/01-what-is-office.md) for scope, and
[docs/10-system/02-trust-model.md](docs/10-system/02-trust-model.md) for the trust chain.

**Out of this repository:** the Headquarters gateway, scheduling, enrollment approval, certificate issuance,
artifact authorization and storage, the host side of the guest broker, result-artifact ingestion, and the fleet
UI. Do not invent behaviour for those; mark it `> **Out of repo:**` and cite the in-repo evidence that
establishes the boundary.

## Build and test

```powershell
dotnet build CSweet.Office.slnx -c Release
dotnet build CSweet.Office.Independent.slnx -c Release -p:UseLocalOfficeContracts=false
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release -p:UseLocalOfficeContracts=false
```

- The primary solution loads the sibling `../CSweet.Office.Contracts` project. The independent solution
  resolves the pinned package instead and is what CI and releases use.
- `UseLocalOfficeContracts` is auto-detected: it becomes `true` whenever the sibling `.csproj` exists on disk.
  Always pass it explicitly when validating the release boundary, or you will test uncommitted contract code.
- Central package management is on. Add a `PackageReference` with no version and put the `PackageVersion` in
  `Directory.Packages.props`.
- There is no `global.json`; the .NET 10 SDK comes from `PATH`.

## Hard rules

1. **Never weaken a security invariant.** They are numbered `SEC-INV-01` … `SEC-INV-23` in
   [docs/20-security/11-security-invariants.md](docs/20-security/11-security-invariants.md). Cite the
   identifier; do not restate or reinterpret the rule.
2. **No workload without a signed Headquarters authorization.** `RuntimeHostProviderClient.CreateAsync` throws
   by contract (`SEC-INV-07`). Do not add an overload, a fast path, or a reachable bypass.
3. **Never reorder authorization validation.** The replay ledger commits only after signature, digest, and time
   validation succeed (`SEC-INV-08`).
4. **No fallback execution path.** An unavailable, uncertified, expired, or unrecognized provider fails closed
   (`SEC-INV-10`). Never add a process-level or shared-kernel substitute.
5. **Helpers stay narrow.** Protocol `1.0`, eight typed operations over stdio, digest re-verified per
   invocation, no shell, no request-supplied paths or mount options (`SEC-INV-20`).
6. **Do not add files under the guest-image fingerprint roots.** `Get-GuestBuildFingerprint` hashes every
   non-`bin`/`obj` file under `src/CSweet.Office.RuntimeGuest`, `src/CSweet.Office.BuilderGuest`,
   `src/CSweet.Office.ToolchainGuest`, `src/CSweet.Office.Runtime.Protocol`, `build/windows-hyperv`,
   `../CSweet.Isolation/tools/LinuxImage`, and `scripts/windows/New-CSweetHyperVTestGuest.ps1`. Adding a file
   forces a full Packer guest rebuild. Documentation for those projects belongs in `docs/`.
7. **Never add a dependency without a central pin**, and never add `CopyToOutputDirectory` to a non-code file —
   builder and toolchain bundles archive the publish output directory verbatim.

## Deliberate behaviours — do not "fix" them

Each has a rationale in [docs/20-security/12-known-limits-and-tradeoffs.md](docs/20-security/12-known-limits-and-tradeoffs.md):

- Assignment validation is implemented twice, at two privilege levels, in `OfficeWorker.ValidateAssignment`
  and `RuntimeHostAuthorizationGate.ValidateAndCommit`.
- `accepted-assignments.json` grows without bound. Pruning it reopens the replay window.
- Helpers exit `0` on typed failures so RuntimeHost keeps the typed error instead of a generic `IOException`.
- The guest tunnel sends an empty `Sequence = 0` frame before relaying, or three parties deadlock.
- The Hyper-V helper's `Logs()` is a stub returning an empty array.
- `ResourceLimits.MaximumDurationSeconds` is not enforced by Office or the helpers.
- `OfficeArtifactCache.ImportAsync` throws `NotSupportedException`.
- Several test classes assert on script **text**, so they pass against a present-but-broken script.
- `SingleFileIso9660.VerifyArtifactDigestAsync` hashes the payload extent, not the whole ISO file.

## Documentation obligation

Every behavioral claim in `docs/` cites a source file and carries a `Verified:` date. When you change behaviour,
update the affected pages and refresh the date. If you cannot, record the discrepancy in the page rather than
leaving a claim that is quietly wrong. The style rules are in
[docs/70-contributing/02-documentation-style.md](docs/70-contributing/02-documentation-style.md).

## Task recipes

Repeatable prompts for the common workflows live in [`.github/prompts/`](.github/prompts), deep procedural
skills in [`.github/skills/`](.github/skills), and path-scoped rules in
[`.github/instructions/`](.github/instructions).
