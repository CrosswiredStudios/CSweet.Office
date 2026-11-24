[CmdletBinding()]
param([Parameter(Mandatory)][string]$Architecture, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$rid = "osx-$Architecture"
$payload = Join-Path $Output "$rid-payload"
# The provider compares the suite and timestamps against the immutable evidence.
# Read the timestamp strings verbatim so fractional seconds survive packaging.
$suite = & jq -er '.certificationSuiteVersion' $env:CSWEET_CERTIFICATION_EVIDENCE
if ($LASTEXITCODE -ne 0) { throw 'Missing certification suite in macOS evidence.' }
$certified = & jq -er '.certifiedAt' $env:CSWEET_CERTIFICATION_EVIDENCE
if ($LASTEXITCODE -ne 0) { throw 'Missing certification time in macOS evidence.' }
$expires = & jq -er '.certificationExpiresAt' $env:CSWEET_CERTIFICATION_EVIDENCE
if ($LASTEXITCODE -ne 0 -or [DateTimeOffset]::Parse($expires) -ne [DateTimeOffset]::Parse($env:CSWEET_CERTIFICATION_VALID_UNTIL)) {
    throw 'The macOS certification evidence must match the protected release expiry.'
}
$arguments = @($payload, $rid, $env:CSWEET_APPLE_SIGNING_IDENTITY, $env:CSWEET_GUEST_KERNEL,
    $env:CSWEET_GUEST_IMAGE, $env:CSWEET_GUEST_IMAGE_SIGNATURE, $env:CSWEET_GUEST_SIGNING_CERTIFICATE,
    $env:CSWEET_GUEST_SIGNING_THUMBPRINT, $env:CSWEET_CERTIFICATION_EVIDENCE, $suite, $certified, $expires)
& bash (Join-Path $root 'scripts\macos\new-runtime-payload.sh') @arguments
if ($LASTEXITCODE -ne 0) { throw 'macOS payload creation failed.' }
$package = Join-Path $Output "csweet-office-$Version-macos-$Architecture.pkg"
& bash (Join-Path $root 'scripts\macos\new-installer-package.sh') $payload $package $Version `
    $env:CSWEET_APPLE_INSTALLER_SIGNING_IDENTITY $env:CSWEET_APPLE_NOTARY_PROFILE
if ($LASTEXITCODE -ne 0) { throw 'macOS package creation failed.' }
