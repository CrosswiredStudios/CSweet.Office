[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('windows','linux','macos')] [string] $OperatingSystem,
    [Parameter(Mandatory)] [ValidateSet('x64','arm64')] [string] $Architecture,
    [Parameter(Mandatory)] [string] $Tag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v(?<version>\d+\.\d+\.\d+)$') { throw 'Release tag must be vMAJOR.MINOR.PATCH.' }
$version = $Matches.version
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = Join-Path $root 'artifacts\release'
New-Item -ItemType Directory -Force $output | Out-Null
dotnet test (Join-Path $root 'CSweet.SatelliteOffice.slnx') -c Release -p:UseLocalSatelliteOfficeContracts=false
if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }
$symbols = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter '*.pdb' -File | Where-Object FullName -Match '\\Release\\'
if ($symbols) { Compress-Archive -Path $symbols.FullName -DestinationPath (Join-Path $output "csweet-satellite-office-$version-$OperatingSystem-$Architecture-symbols.zip") -Force }

# Certification produces immutable guest images/evidence before this entry point. Hardened
# runners provide their paths and signing identities as protected environment variables.
$required = @('CSWEET_GUEST_IMAGE','CSWEET_GUEST_IMAGE_SIGNATURE','CSWEET_GUEST_SIGNING_CERTIFICATE',
    'CSWEET_GUEST_SIGNING_THUMBPRINT','CSWEET_CERTIFICATION_EVIDENCE','CSWEET_CERTIFICATION_VALID_UNTIL')
foreach ($name in $required) { if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Missing protected release input $name." } }

switch ($OperatingSystem) {
    'windows' {
        if ($Architecture -ne 'x64') { throw 'Windows release currently supports x64 only.' }
        $payload = Join-Path $output 'windows-x64-payload'
        & (Join-Path $root 'scripts\windows\New-CSweetWindowsRuntimePayload.ps1') `
            -GuestImage $env:CSWEET_GUEST_IMAGE -GuestImageSignature $env:CSWEET_GUEST_IMAGE_SIGNATURE `
            -GuestImageSigningCertificate $env:CSWEET_GUEST_SIGNING_CERTIFICATE `
            -GuestImageSigningCertificateThumbprint $env:CSWEET_GUEST_SIGNING_THUMBPRINT `
            -CertificationEvidence $env:CSWEET_CERTIFICATION_EVIDENCE -CertificationSuiteVersion 'production-v1' `
            -CertifiedAt ([DateTimeOffset]::UtcNow) -CertificationExpiresAt $env:CSWEET_CERTIFICATION_VALID_UNTIL `
            -PackageVersion $version -OutputRoot $payload
        & (Join-Path $root 'scripts\windows\New-CSweetSatelliteOfficeMsi.ps1') -PayloadRoot $payload `
            -OutputPath (Join-Path $output "csweet-satellite-office-$version-windows-x64.msi") -Version $version `
            -CertificateThumbprint $env:CSWEET_WINDOWS_SIGNING_THUMBPRINT
    }
    'linux' {
        & (Join-Path $root 'scripts\release\Invoke-LinuxRelease.ps1') -Architecture $Architecture -Version $version -Output $output
    }
    'macos' {
        & (Join-Path $root 'scripts\release\Invoke-MacRelease.ps1') -Architecture $Architecture -Version $version -Output $output
    }
}

if (Get-Command syft -ErrorAction SilentlyContinue) { syft scan "dir:$output" -o "spdx-json=$(Join-Path $output "$OperatingSystem-$Architecture.spdx.json")" }
else { throw 'syft is required to produce the release SBOM.' }
