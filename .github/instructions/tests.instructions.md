---
description: "Conventions for the xUnit test project: hand-written fakes, no mocking library, OS guards, script-text assertions that do not prove behaviour, and which classes pin which security invariant."
applyTo: "tests/**"
---

# Test conventions

Read [`docs/50-development/07-testing-guide.md`](../../docs/50-development/07-testing-guide.md) and
[`docs/80-reference/tests.md`](../../docs/80-reference/tests.md) before adding or changing a test.

## Framework and setup

- xUnit v2 (`xunit 2.9.3`, `xunit.runner.visualstudio 3.1.5`, `Microsoft.NET.Test.Sdk 18.7.0`).
- `<Using Include="Xunit" />` is declared in the csproj, so no `using Xunit;` in files.
- `[Fact]` and `[Theory]` with `[InlineData]` only. There is no `[Trait]`, no `SkippableFact`, and no
  `Skip=`. Tests that cannot run on the current OS **early-return** instead — which means a green run does not
  mean those tests executed.
- All tests live in the single flat namespace `CSweet.Office.Tests`.
- No mocking library. Use the hand-written fakes in the projects (`InMemoryAgentIsolationProvider`,
  `InMemoryBuilderArtifactResultStore`) or write a small local fake.
- `InternalsVisibleTo` is declared per project in the csproj, which is how tests reach `internal` types.

## Names and structure

- Name tests `Thing_Behaviour`, for example `Authenticator_LoadsKeyCreatedAfterControlPlaneStartup`.
- Prefer asserting exact, load-bearing string literals — several tests assert on specific error codes and
  message fragments, and those assertions are the specification.
- Split `[Theory]` data out with `[InlineData]` rather than writing near-duplicate facts.

## What a green run does not prove

| Class | Why |
|---|---|
| `LinuxInstallationTests` | Asserts on the **text** of `scripts/linux/*.sh`. Passes against a present-but-broken script. |
| `WindowsHyperVOnboardingTests` | Mostly script-text assertions; the one behavioural case shells out to Windows PowerShell 5.1 (`powershell.exe`, not `pwsh`) and returns early on non-Windows. |
| `RuntimeHostRpcIntegrationTests` | Exercises Windows named pipes and skips off-Windows. |
| `OfficeCertificateTlsTests` | Uses `RSACng` on Windows and `RSA.Create` elsewhere, so the code path differs by platform. |

If you change a script, run it. If you change a Windows-only path, you need Windows.

## Running

```powershell
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release -p:UseLocalOfficeContracts=false
```

Pass the switch explicitly, or `UseLocalOfficeContracts` will silently resolve the sibling checkout and you
will test contract code that is not in the released package.

## When you add a security control

Add the test in the same pull request, name the invariant it pins in a comment, and add the class to the
invariant table in [`docs/80-reference/tests.md`](../../docs/80-reference/tests.md) and to the `Pinned by` line
of the matching entry in
[`docs/20-security/11-security-invariants.md`](../../docs/20-security/11-security-invariants.md).

Some invariants currently have **no** test — the reference page lists them explicitly. Adding coverage for those
is worthwhile work.
