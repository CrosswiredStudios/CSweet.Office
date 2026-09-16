---
description: "Prepare an Office release: reconcile the three version truths, confirm certification inputs, write release notes, and hand off to the hardened workflow. Does not sign or publish."
mode: agent
---

# Prepare a release

**This prompt never signs, publishes, or produces a release asset.** Signing and certification run only on the
hardened platform workflows. Everything here is preparation.

## 1. Reconcile the three version truths

| Value | Location | Expected |
|---|---|---|
| Office version | `VersionPrefix` in `Directory.Build.props` | the version you are releasing |
| Contracts pin | `CSweet.Office.Contracts` `PackageVersion` in `Directory.Packages.props` | the released contracts version this Office requires |
| Release tag | the git tag you will create | `v<VersionPrefix>`, exactly |

`Get-OfficeReleaseMetadata.ps1` is the only thing that enforces agreement: the tag must equal `VersionPrefix`.
Office tags are independent of C-Sweet headquarters tags — do not couple them.

Read [`docs/60-release/01-versioning-and-compatibility.md`](../../docs/60-release/01-versioning-and-compatibility.md).

## 2. Verify the boundary before tagging

```powershell
dotnet build CSweet.Office.Independent.slnx -c Release -p:UseLocalOfficeContracts=false
dotnet test tests/CSweet.Office.Tests/CSweet.Office.Tests.csproj -c Release -p:UseLocalOfficeContracts=false
```

The switch is not optional. Without it the build may resolve a local contracts checkout and the release is
unverified.

## 3. Confirm certification inputs exist and are current

The release pipeline consumes `CSWEET_GUEST_IMAGE`, `CSWEET_GUEST_IMAGE_SIGNATURE`,
`CSWEET_GUEST_SIGNING_CERTIFICATE`, `CSWEET_GUEST_SIGNING_THUMBPRINT`, `CSWEET_CERTIFICATION_EVIDENCE`, and
`CSWEET_CERTIFICATION_VALID_UNTIL`, plus the per-platform toolchain variables (`CSWEET_FIRECRACKER`,
`CSWEET_JAILER`, `CSWEET_GUEST_KERNEL`, `CSWEET_GUEST_INITRD`).

Check that the evidence is newer than any guest-image or helper change. A stale helper digest invalidates the
certified configuration (`SEC-INV-10`).

## 4. Write the release notes

Create `releases/<version>.md`. Match the existing style — a short title, the user-visible change, any
deployment ordering requirement, and any prerequisite such as "drain first" or "rebuild guest images". See
[`docs/60-release/06-release-notes-process.md`](../../docs/60-release/06-release-notes-process.md) for the
worked examples from 0.4.0 through 0.5.3.

## 5. Check the impact matrix

Walk [`docs/70-contributing/03-change-impact-matrix.md`](../../docs/70-contributing/03-change-impact-matrix.md)
for everything that changed since the last release and confirm the guest image, payload, certification, and
release notes all reflect it.

## 6. Hand off

State what the operator must do in C-Sweet before upgrading — typically draining to zero active assignments —
and confirm that the release notes say so. Identity is preserved on upgrade only after drain with zero active
assignments (`SEC-INV-18`).

Report what you changed, what you verified, and what still requires the hardened workflow.
