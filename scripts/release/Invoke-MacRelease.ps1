[CmdletBinding()]
param([Parameter(Mandatory)][string]$Architecture, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$rid = "osx-$Architecture"
$payload = Join-Path $Output "$rid-payload"
$arguments = @($payload, $rid, $env:CSWEET_APPLE_SIGNING_IDENTITY, $env:CSWEET_GUEST_KERNEL,
    $env:CSWEET_GUEST_IMAGE, $env:CSWEET_GUEST_IMAGE_SIGNATURE, $env:CSWEET_GUEST_SIGNING_CERTIFICATE,
    $env:CSWEET_GUEST_SIGNING_THUMBPRINT, $env:CSWEET_CERTIFICATION_EVIDENCE, 'production-v1',
    [DateTimeOffset]::UtcNow.ToString('O'), $env:CSWEET_CERTIFICATION_VALID_UNTIL)
& bash (Join-Path $root 'scripts\macos\new-runtime-payload.sh') @arguments
if ($LASTEXITCODE -ne 0) { throw 'macOS payload creation failed.' }
$package = Join-Path $Output "csweet-satellite-office-$Version-macos-$Architecture.pkg"
& bash (Join-Path $root 'scripts\macos\new-installer-package.sh') $payload $package $Version `
    $env:CSWEET_APPLE_INSTALLER_SIGNING_IDENTITY $env:CSWEET_APPLE_NOTARY_PROFILE
if ($LASTEXITCODE -ne 0) { throw 'macOS package creation failed.' }
