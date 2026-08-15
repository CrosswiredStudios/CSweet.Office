[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $ContractsVersion,
    [Parameter(Mandatory)] [string] $AssetDirectory,
    [Parameter(Mandatory)] [string] $OutputPath,
    [string] $ProtocolVersion = '1.0',
    [string] $GuestImageDigest,
    [DateTimeOffset] $CertificationValidUntil
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $ContractsVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Versions must use MAJOR.MINOR.PATCH.' }
if ($GuestImageDigest -and $GuestImageDigest -notmatch '^sha256:[0-9a-f]{64}$') { throw 'GuestImageDigest is invalid.' }
$definitions = @(
    @{ Pattern = '*.msi'; Os = 'windows'; Type = 'msi'; Signature = 'authenticode' },
    @{ Pattern = '*.deb'; Os = 'linux'; Type = 'deb'; Signature = 'openpgp' },
    @{ Pattern = '*.rpm'; Os = 'linux'; Type = 'rpm'; Signature = 'openpgp' },
    @{ Pattern = '*.pkg'; Os = 'macos'; Type = 'pkg'; Signature = 'developer-id' })
$assets = foreach ($definition in $definitions) {
    foreach ($file in Get-ChildItem -LiteralPath $AssetDirectory -Filter $definition.Pattern -File) {
        $architecture = if ($file.Name -match '(arm64|aarch64)') { 'arm64' } else { 'x64' }
        $payloadOs = if ($definition.Os -eq 'macos') { 'osx' } else { $definition.Os }
        $runtimeManifest = Get-ChildItem -LiteralPath $AssetDirectory -Filter "*$payloadOs*$architecture*-payload" -Directory |
            ForEach-Object { Join-Path $_.FullName 'runtime-manifest.json' } | Where-Object { Test-Path $_ } | Select-Object -First 1
        $runtime = if ($runtimeManifest) { Get-Content $runtimeManifest -Raw | ConvertFrom-Json } else { $null }
        $assetGuestDigest = if ($runtime) { [string]$runtime.guestImageDigest } else { $GuestImageDigest }
        $assetCertificationUntil = if ($runtime -and $runtime.certificationExpiresAt) { [DateTimeOffset]$runtime.certificationExpiresAt } else { $CertificationValidUntil }
        if ($assetGuestDigest -notmatch '^sha256:[0-9a-f]{64}$' -or $assetCertificationUntil -eq [DateTimeOffset]::MinValue) {
            throw "Release evidence is missing for $($file.Name)."
        }
        [ordered]@{
            operatingSystem = $definition.Os; architecture = $architecture; packageType = $definition.Type
            url = "https://github.com/CrosswiredStudios/CSweet.Office/releases/download/v$Version/$($file.Name)"
            size = $file.Length; sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            signature = [ordered]@{ kind = $definition.Signature; keyId = 'c-sweet-release'; timestamp = $null }
            guestImageDigest = $assetGuestDigest
            certificationValidUntil = $assetCertificationUntil.ToUniversalTime().ToString('O')
        }
    }
}
if (@($assets).Count -eq 0) { throw 'No installer assets were found.' }
$manifest = [ordered]@{ schemaVersion = 1; officeVersion = $Version; contractsVersion = $ContractsVersion; protocolVersion = $ProtocolVersion; assets = @($assets) }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
