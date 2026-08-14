[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $GuestImage,
    [Parameter(Mandatory = $true)] [string] $GuestImageSignature,
    [Parameter(Mandatory = $true)] [string] $GuestImageSigningCertificate,
    [Parameter(Mandatory = $true)] [string] $GuestImageSigningCertificateThumbprint,
    [Parameter(Mandatory = $true)] [string] $CertificationEvidence,
    [Parameter(Mandatory = $true)] [string] $CertificationSuiteVersion,
    [Parameter(Mandatory = $true)] [datetimeoffset] $CertifiedAt,
    [Parameter(Mandatory = $true)] [string] $PackageVersion,
    [string] $CertificationExpiresAt,
    [string] $RuntimeIdentifier = 'win-x64',
    [string] $OutputRoot = "$PSScriptRoot\..\..\artifacts\windows-runtime\payload"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PackageVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') { throw 'PackageVersion is invalid.' }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ([IO.Path]::GetPathRoot($OutputRoot) -eq $OutputRoot) { throw 'OutputRoot cannot be a filesystem root.' }
foreach ($source in @($GuestImage, $GuestImageSignature, $GuestImageSigningCertificate, $CertificationEvidence)) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required input is missing: $source" }
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$runtimeRoot = Join-Path $OutputRoot 'runtime'
$helperRoot = Join-Path $OutputRoot 'helper'
$nodeRoot = Join-Path $OutputRoot 'node'
$imageRoot = Join-Path $OutputRoot 'images'
$certificateRoot = Join-Path $OutputRoot 'certificates'
$certificationRoot = Join-Path $OutputRoot 'certification'
New-Item -ItemType Directory -Path $runtimeRoot, $helperRoot, $nodeRoot, $imageRoot, $certificateRoot, $certificationRoot -Force | Out-Null

dotnet publish (Join-Path $repositoryRoot 'src\CSweet.SatelliteOffice.RuntimeHost\CSweet.SatelliteOffice.RuntimeHost.csproj') -c Release -r $RuntimeIdentifier --self-contained true -o $runtimeRoot
if ($LASTEXITCODE -ne 0) { throw 'RuntimeHost publish failed.' }
dotnet publish (Join-Path $repositoryRoot 'src\CSweet.SatelliteOffice.Runtime.HyperV.Helper\CSweet.SatelliteOffice.Runtime.HyperV.Helper.csproj') -c Release -r $RuntimeIdentifier --self-contained true -o $helperRoot
if ($LASTEXITCODE -ne 0) { throw 'Hyper-V helper publish failed.' }
dotnet publish (Join-Path $repositoryRoot 'src\CSweet.SatelliteOffice.Node\CSweet.SatelliteOffice.Node.csproj') -c Release -r $RuntimeIdentifier --self-contained true -o $nodeRoot
if ($LASTEXITCODE -ne 0) { throw 'SatelliteOffice publish failed.' }
$publishedNodeExecutable = Join-Path $nodeRoot 'CSweet.SatelliteOffice.Node.exe'
try {
    $publishedNodeVersion = [Version](Get-Item -LiteralPath $publishedNodeExecutable).VersionInfo.FileVersion
} catch {
    throw 'The published Satellite Office executable version is invalid.'
}
if ($publishedNodeVersion -lt [Version]'1.0.2.0') {
    throw "The payload generator published Satellite Office $publishedNodeVersion. Version 1.0.2 or later is required for privileged signed-assignment enforcement."
}

$installedImage = Join-Path $imageRoot 'csweet-agent-guest.vhdx'
$installedSignature = "$installedImage.sig"
$installedCertificate = Join-Path $certificateRoot 'guest-image-signing.cer'
$installedEvidence = Join-Path $certificationRoot 'windows-hyperv.json'
Copy-Item -LiteralPath $GuestImage -Destination $installedImage -Force
Copy-Item -LiteralPath $GuestImageSignature -Destination $installedSignature -Force
Copy-Item -LiteralPath $GuestImageSigningCertificate -Destination $installedCertificate -Force
Copy-Item -LiteralPath $CertificationEvidence -Destination $installedEvidence -Force

function Digest([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$outputPrefix = $OutputRoot.TrimEnd('\') + '\'
$files = @(Get-ChildItem -LiteralPath $OutputRoot -File -Recurse | Where-Object { $_.Name -ne 'runtime-manifest.json' } | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($outputPrefix.Length).Replace('\', '/')
        sha256 = Digest $_.FullName
    }
})
$manifest = [ordered]@{
    schemaVersion = 1
    packageVersion = $PackageVersion
    satelliteOfficeVersion = $publishedNodeVersion.ToString(3)
    runtimeHostExecutable = "runtime/CSweet.SatelliteOffice.RuntimeHost.exe"
    helperExecutable = "helper/CSweet.SatelliteOffice.Runtime.HyperV.Helper.exe"
    satelliteOfficeExecutable = "node/CSweet.SatelliteOffice.Node.exe"
    guestImage = "images/csweet-agent-guest.vhdx"
    guestImageDigest = "sha256:$(Digest $installedImage)"
    guestImageSignature = "images/csweet-agent-guest.vhdx.sig"
    guestImageSigningCertificate = "certificates/guest-image-signing.cer"
    guestImageSigningCertificateThumbprint = $GuestImageSigningCertificateThumbprint
    certificationSuiteVersion = $CertificationSuiteVersion
    certificationEvidence = "certification/windows-hyperv.json"
    certificationEvidenceDigest = "sha256:$(Digest $installedEvidence)"
    certifiedAt = $CertifiedAt.ToUniversalTime().ToString('O')
    certificationExpiresAt = if ([String]::IsNullOrWhiteSpace($CertificationExpiresAt)) { $null } else { $CertificationExpiresAt }
    files = $files
}
[IO.File]::WriteAllText((Join-Path $OutputRoot 'runtime-manifest.json'),
    ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Host "Windows RuntimeHost payload created at $OutputRoot"
