# Release notes

**Audience:** anyone cutting a release, and anyone reading what changed between two Office versions.

Release notes live in `releases/`, one Markdown file per version, named for the version: `releases/0.4.0.md`,
`releases/0.5.0.md`, `releases/0.5.1.md`, `releases/0.5.2.md`, `releases/0.5.3.md`. They are the human-facing
record that accompanies a tag. `hosted-release.yml` checks for `releases/<version>.md` before building
and uses its contents as the GitHub Release body. Include the notes in the version-bump push so
operators receive deployment ordering and prerequisites with the published artifacts.

## The convention

| Rule | Detail |
|---|---|
| One file per released version | `releases/<MAJOR.MINOR.PATCH>.md`, matching `VersionPrefix` in `Directory.Build.props` exactly. |
| Title | `# Office <version>`. |
| Body | A flat bullet list of user-visible changes, one change per bullet, written for an administrator who operates the fleet. |
| Deployment ordering | When the release depends on headquarters work, state the order explicitly. |
| Prerequisites | State what must be true before the upgrade: drain first, rebuild guest images, take a recovery package, and so on. |
| Non-goals | Where a change could be misread as opening a capability, state what remains unsupported. |

## Worked example

The five released notes as they are written today, one line each:

| Version | What it changed |
|---|---|
| 0.4.0 | Live certificate renewal during control sessions, plus key-proof recovery of an expired or superseded certificate, with a validation step before the saved identity is replaced. |
| 0.5.0 | An independent Windows recovery service managed through C-Sweet: guided, administrator-authorized repair that preserves identity and settings, and a separate removal and fresh-enrollment flow. |
| 0.5.1 | Three platform-provided workspace transfer limits are allowed in guest workload environments so SDK 3.45.1 agents can start; other unapproved environment keys remain rejected. |
| 0.5.2 | The running Office assembly version is reported on every heartbeat, so headquarters can detect an in-place update without re-enrollment; the contracts pin moves to 0.7.0 and release metadata is generated from it. |
| 0.5.3 | An identity-preserving upgrade is allowed for a drained Office when only saved authorization records and powered-off VMs remain, while every other blocking condition stays in place. |

The same notes show each of the required content types:

| Content type | Example |
|---|---|
| User-visible change | 0.5.1: *"Allow the three platform-provided workspace transfer limits in guest workload environments so SDK 3.45.1 agents can start."* |
| Deployment ordering | 0.4.0: *"Require Office.Contracts 0.6.0. Upgrade headquarters first, then drain Office and use an identity-preserving upgrade after its active assignments reach zero."* |
| Prerequisite for the image | 0.5.1: *"Runtime guest images must be rebuilt and updated to include this change."* |
| Prerequisite for recovery | 0.5.0: *"Older installations require the current recovery package and Windows administrator approval once."* |
| Non-goal | 0.5.0: *"No arbitrary remote commands, unattended removal, or unsigned remote payload downloads are supported."* |
| Verification statement | 0.4.0: *"Windows recovery and TLS regression checks are included. Signed installer publishing and Linux/macOS platform certification remain part of the hardened release workflows."* |

## Writing the next one

1. Start the file in the same change that moves `VersionPrefix`, before pushing to `main`. The tag must
   equal `VersionPrefix`, and the file name must equal the tag's version — see
   [01-versioning-and-compatibility.md](01-versioning-and-compatibility.md).
2. Write bullets in the imperative or declarative present tense, as the existing files do. Describe behaviour
   an operator will observe, not implementation detail.
3. If the release needs a specific `CSweet.Office.Contracts` version on the headquarters side, say so and say
   which side moves first.
4. If the release requires a guest image rebuild, a re-certification, or a recovery package, say so. The
   fingerprint rule that makes image rebuilds necessary is in `docs/30-workloads/06-guest-images.md`.
5. If the release narrows or preserves an existing restriction, state the restriction that remains. 0.5.3 is
   the model: one bullet for what is now allowed, one for what is still refused, one for the paths that were
   left alone.
6. Do not restate security invariants. Cite `SEC-INV-nn` from
   [../20-security/11-security-invariants.md](../20-security/11-security-invariants.md) instead.

## Sources

`releases/{0.4.0,0.5.0,0.5.1,0.5.2,0.5.3}.md`, `Directory.Build.props`, `scripts/release/Get-OfficeReleaseMetadata.ps1`,
`.github/workflows/{release,hosted-release}.yml`, `docs/20-security/11-security-invariants.md`, `docs/30-workloads/06-guest-images.md`.

Verified: 2026-09-19.
