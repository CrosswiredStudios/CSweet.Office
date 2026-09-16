---
description: "Rebuild the Windows Office payload and certification evidence after host-code changes: fingerprint check, guest image cache, smoke test, guest image signing, payload assembly, and install."
mode: agent
---

# Rebuild the Windows payload

Use this after changing `RuntimeHost`, `Node`, `Configurator`, or the Hyper-V helper. Those are **not** part of
the guest build fingerprint, so the cached guest image stays valid — but the payload must be regenerated.

Do not use this to change guest code. That path is
[`docs/50-development/09-guest-image-changes.md`](../../docs/50-development/09-guest-image-changes.md).

## Before you start

- You need an elevated PowerShell session. The script self-elevates, and Windows will prompt.
- The sibling `CSweet.Isolation` repository must exist; the script imports `CSweet.LinuxImage` from it.
- Decide whether you need `-RebuildGuest`. Host-only changes do not.

## Steps

1. Run the isolation test bootstrap, letting it reuse the cached guest image:

   ```powershell
   .\scripts\windows\Initialize-CSweetWindowsIsolationTest.ps1
   ```

   It publishes the helper, probe, and smoke binaries; runs the certification smoke test; signs the guest
   image with the development signer; assembles the payload; and installs, unless you pass `-SkipInstall`.

   If it exits `0` with a `restart-required` progress record, reboot and re-run.

2. Select the newest complete payload. **Never** select an older `certification-*` directory after a rebuild:

   ```powershell
   $payload = Get-ChildItem '.\artifacts\windows-test' -Directory -Filter 'certification-*' |
       Where-Object { Test-Path (Join-Path $_.FullName 'payload\runtime-manifest.json') } |
       Sort-Object LastWriteTime -Descending |
       Select-Object -First 1 |
       ForEach-Object { Join-Path $_.FullName 'payload' }
   ```

3. Verify the payload is not stale:

   ```powershell
   $manifest = Get-Content (Join-Path $payload 'runtime-manifest.json') -Raw | ConvertFrom-Json
   $node = Get-Item (Join-Path $payload $manifest.officeExecutable)
   if ([Version]$node.VersionInfo.FileVersion -lt [Version]'0.1.0.0') {
       throw "Stale payload: Office $($node.VersionInfo.FileVersion)"
   }
   ```

4. Install:

   ```powershell
   .\scripts\windows\Install-CSweetOffice.ps1 -PayloadRoot $payload -ControlPlaneUrl 'https://localhost:54782'
   ```

## Traps to keep in mind

- The RuntimeHost installer **skips files whose installed SHA-256 already matches** the manifest. Re-running it
  against a stale payload is a silent no-op, not a refresh.
- The installer gates on a payload version of at least `0.1.0.0` while its message text names a different
  version. Trust the threshold, not the message.
- An upgrade requires the office to be draining with zero active assignments; a first install always enrolls a
  fresh identity.
- Never run `Clear-CSweetGeneratedHyperVImages.ps1` with VMs running.

Reference: [`docs/50-development/04-windows-dev-loop.md`](../../docs/50-development/04-windows-dev-loop.md),
[`docs/60-release/02-payload-and-manifest.md`](../../docs/60-release/02-payload-and-manifest.md).
