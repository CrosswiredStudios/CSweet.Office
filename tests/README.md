# tests

The single xUnit test project for the execution plane: `CSweet.Office.Tests`, 25 files and 135 test methods, referencing the runtime libraries directly. There is no test project per library and no fixtures directory; helpers are private nested classes inside each test class.

Part of the C-Sweet Office repository. Testing guide: [`docs/50-development/07-testing-guide.md`](../docs/50-development/07-testing-guide.md). Per-class reference and the invariant coverage table: [`docs/80-reference/tests.md`](../docs/80-reference/tests.md).

## Running

```powershell
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release -p:UseLocalOfficeContracts=false
```

Pass `-p:UseLocalOfficeContracts=false` explicitly, or the build silently resolves a sibling `CSweet.Office.Contracts` checkout and you test contract code that is not in the released package.

## Conventions

- xUnit v2 (`xunit 2.9.3`, `xunit.runner.visualstudio 3.1.5`, `Microsoft.NET.Test.Sdk 18.7.0`), one flat namespace, `[Fact]` and `[Theory]` with `[InlineData]` only. `<Using Include="Xunit" />` is declared in the csproj.
- No mocking library. Use the shipped hand-written doubles (`InMemoryAgentIsolationProvider`, `InMemoryBuilderArtifactResultStore`) or write a small local fake.
- `InternalsVisibleTo` is declared per project, which is how tests reach `internal` types.
- Tests that cannot run on the current OS **early-return** rather than skip, so a green run does not prove those tests executed.
- Name tests `Thing_Behaviour`, and assert exact error codes and message fragments where they are load-bearing.

## What a green run does not prove

| Class | Why |
|---|---|
| `LinuxInstallationTests` | Asserts on the **text** of `scripts/linux/*.sh`; passes against a present-but-broken script. |
| `WindowsHyperVOnboardingTests` | Mostly script-text assertions; the one behavioural case shells out to Windows PowerShell 5.1 and returns early on non-Windows. |
| `RuntimeHostRpcIntegrationTests` | Exercises Windows named pipes and skips off-Windows. |
| `OfficeCertificateTlsTests` | Uses `RSACng` on Windows and `RSA.Create` elsewhere, so the code path differs by platform. |

If you change a script, run it. If you change a Windows-only path, you need Windows.

## Upgrade-probe harness

`scripts/tests/Test-OfficeUpgradeProbe.ps1` stubs the PowerShell host, builds a fake install tree under `%TEMP%\office-probe-test-<guid>`, and asserts the eight `Get-CSweetOfficeRecoveryState.ps1` state transitions. `WindowsHyperVOnboardingTests.UpgradeProbeDistinguishesPreservedStateFromExecutingWork` runs it as a child process and requires exit code `0`; it can also be run by hand:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\tests\Test-OfficeUpgradeProbe.ps1
```

Expected success line: `Passed 8 upgrade probe scenarios.`, exit code `0`.
