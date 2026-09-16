# PROP-08 — Narrow interactive read access to `runtime-host.key`

**Status:** proposal — not implemented. **Area:** security (local credential exposure). **Effort:** medium to
large, because the audit comes first.

**Tooling cost:** installer and repair scripts (payload-staged on Windows). No guest or certification impact.

**Invariant interactions:** `SEC-INV-12` pins the **pipe** DACL (clients get exactly
`ReadWrite | Synchronize | CreateNewInstance`) — this proposal does not touch the pipe. `SEC-INV-19` bounds
the maintenance service, which is a candidate home for any interactive flow that still needs the key.
`SEC-INV-18` governs what `reconnect` deletes.

## Problem

On Windows the shared RPC key is readable by the interactive control-plane user:

- `runtime-host.key` grants `R` to `ControlPlaneUserSid`, the Node SID, and the RuntimeHost SID, plus full
  control to `SYSTEM` and `Administrators`
  ([`docs/10-system/06-process-network-and-storage-surface.md`](../docs/10-system/06-process-network-and-storage-surface.md),
  [`docs/80-reference/file-layout.md`](../docs/80-reference/file-layout.md)).
- The recorded rationale is that "the interactive installer and the guided-recovery flows need to authenticate
  local RPC calls" and that defence in depth holds because workload creation still requires a Headquarters
  signature ([`docs/20-security/12-known-limits-and-tradeoffs.md`](../docs/20-security/12-known-limits-and-tradeoffs.md),
  "The local RPC shared key is readable by the interactive control-plane user").

Two facts make the grant worth revisiting:

1. Neither Linux nor macOS grants an interactive user access to the key (`root:csweet-runtime 0640`,
   `root:_csweet 0640`), so Windows is the outlier.
2. The likely interactive consumer is not obvious from the code. `CSweet.Office.Configurator` — the browser
   handoff and maintenance-service host — has no project references and no runtime-library usage
   ([its README](../src/CSweet.Office.Configurator/README.md)); the installer itself runs elevated, and
   `Administrators` already has full control. The documented consumer may therefore be historical, or may sit
   in a script outside the obvious places (for example a diagnostics script).

The key is the credential; the pipe right without the key cannot produce a valid HMAC frame. Narrowing the
file ACL therefore removes the ability to even attempt an authenticated conversation.

## User story

**Scenario.** A workstation is shared between normal work and an Office install. A local unprivileged process
reads `%ProgramData%\CSweet\Office\runtime-host.key`, and its user later opens a handshake against the
RuntimeHost pipe. It cannot create work — the authorization gate still demands a Headquarters signature — but
it can speak the protocol, drive probes, and attempt handle operations at the privileged boundary.

> As a security reviewer, I want the shared RPC key readable only by the identities that authenticate with
> it, so that a local user cannot even begin a forged RPC conversation.

## Recommended fix

Audit first, then narrow:

1. **Audit.** Enumerate every consumer of `runtime-host.key`: the installer and repair scripts, the Node and
   RuntimeHost services, the Configurator and the maintenance service, and any operational script under
   `scripts/` (for example diagnostics). Record for each whether it runs as a service identity, an elevated
   administrator, or the interactive user.
2. **If no interactive consumer is real:** remove the `ControlPlaneUserSid` ACE from
   `Install-CSweetOfficeRuntimeHost.ps1` and `Repair-CSweetOfficeRuntimeHostAccess.ps1`, and update the
   file-layout and privilege-model pages.
3. **If a flow genuinely needs it:** prefer moving that flow behind the maintenance service
   (`SEC-INV-19` bounds what it may accept) or giving it a scoped credential, over leaving the shared key
   readable. The documented warning — "verify the installer, the repair path, and the maintenance service
   still function" — is the acceptance test: run first install, `reconnect`, repair, and upgrade afterwards.
4. Do not touch the pipe DACL or the `AllowedClientSid` configuration; `SEC-INV-12` and the regression test
   `WindowsPipeSecurity_GrantsExactDuplexClientConnectionRights` pin them.

## Changes required

| Area | Change | Notes |
|---|---|---|
| `scripts/windows/Install-CSweetOfficeRuntimeHost.ps1` | Remove or narrow the interactive ACE on the key file | The ACL block is asserted by script-text tests; update them together |
| `scripts/windows/Repair-CSweetOfficeRuntimeHostAccess.ps1` | Match the installer | Repair rewrites the same ACLs |
| Tests | Update the script-text assertions; add a behavioural Windows ACL test if the installer's ACL helper can be exercised directly | Current coverage is script text; this is a chance to strengthen it |
| Docs | [`docs/10-system/06-process-network-and-storage-surface.md`](../docs/10-system/06-process-network-and-storage-surface.md), [`docs/20-security/{05-local-rpc-boundary.md,06-host-privilege-model.md,12-known-limits-and-tradeoffs.md}`](../docs/20-security/05-local-rpc-boundary.md), [`docs/80-reference/file-layout.md`](../docs/80-reference/file-layout.md) | Remove or rewrite the "interactive user" rows and the known-limits entry |

## What must not change

- The pipe DACL and its exact client rights (`SEC-INV-12`).
- The HMAC authentication envelope, the nonce rules, and the signed responses (`SEC-INV-13`, `SEC-INV-14`).
- The maintenance service's constraints (`SEC-INV-19`).
- The key's size, encoding, reparse-point rejection, and re-read semantics
  (`RuntimeHostAuthenticationTests`).

## Verification

1. Full installer acceptance run on Windows: first install, `reconnect`, repair, upgrade — with the narrowed
   ACL.
2. Probe as a non-granted local user: the key file must be unreadable, and an RPC attempt without the key must
   be rejected with no response (`SEC-INV-13`).
3. Keep the Windows script-text and named-pipe integration tests green.

## Sources

`scripts/windows/{Install-CSweetOfficeRuntimeHost.ps1,Repair-CSweetOfficeRuntimeHostAccess.ps1}`,
`src/CSweet.Office.{Node,RuntimeHost}/Program.cs`, `src/CSweet.Office.Runtime.Protocol/RuntimeHostAuthentication.cs`,
`docs/10-system/06-process-network-and-storage-surface.md`,
`docs/20-security/{05-local-rpc-boundary.md,06-host-privilege-model.md,12-known-limits-and-tradeoffs.md}`,
`docs/80-reference/file-layout.md`, `tests/CSweet.Office.Tests/WindowsHyperVOnboardingTests.cs`.

Verified: 2026-09-15.
