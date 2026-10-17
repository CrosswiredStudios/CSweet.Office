param([Parameter(Mandatory)][string] $Version, [Parameter(Mandatory)][string] $InputRoot,
    [Parameter(Mandatory)][string] $OutputRoot, [switch] $WindowsOnly,
    [ValidateRange(1, 1073741824)][long] $PartSizeBytes = 1GB)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/OfficeBundleTransport.ps1"
$release = & "$PSScriptRoot/Get-OfficeReleaseMetadata.ps1" -Version $Version
$InputRoot = [IO.Path]::GetFullPath($InputRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'Release packaging requires a new output directory.' }
New-Item -ItemType Directory $OutputRoot -Force | Out-Null
$windows = "$InputRoot/binaries-win-x64"
$linux = "$InputRoot/binaries-linux-x64"
$images = "$InputRoot/guest-images"
$guestInput = if ($WindowsOnly) { "$InputRoot/windows-guest" } else { "$images/windows-guest" }
$profilePath = Join-Path $guestInput 'guest-image-profile.json'
if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) { throw 'The Windows guest image has no development-profile build evidence.' }
$guestProfile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
if ($guestProfile.environmentProfile -cne 'software-development-polyglot-v1' -or
    $guestProfile.guestBrokerProtocolVersion -cne '1.1' -or
    $guestProfile.guestImageSha256 -cnotmatch '^[0-9a-f]{64}$' -or
    (Get-FileHash -LiteralPath (Join-Path $guestInput 'csweet-agent-guest.vhdx') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $guestProfile.guestImageSha256) {
    throw 'The Windows guest image does not match its development-profile build evidence.'
}
if (Test-Path -LiteralPath (Join-Path $windows 'guest')) { throw 'Guest assembly requires a fresh binary artifact directory.' }
if ($WindowsOnly) {
    Copy-Item "$InputRoot/windows-guest" "$windows/guest" -Recurse
} else {
    Copy-Item "$images/windows-guest" "$windows/guest" -Recurse
}
if (-not $WindowsOnly) {
    Copy-Item "$images/linux-guest" "$linux/guest" -Recurse
    Copy-Item "$images/linux-tools" "$linux/tools" -Recurse
    # Restored artifact permissions are intentionally normalized before packaging on Linux.
    & chmod -R u+rwX,go+rX $linux
    if ($LASTEXITCODE -ne 0) { throw 'Linux bundle permission normalization failed.' }
    # .NET apphost names contain dots, so FileInfo.Extension cannot identify executables.
    $linuxExecutables = @(
        'tools/firecracker', 'tools/jailer',
        'components/runtime/CSweet.Office.RuntimeHost', 'components/node/CSweet.Office.Node',
        'components/helper/CSweet.Office.Runtime.Firecracker.Helper',
        'components/smoke/CSweet.Office.WindowsSmokeTest', 'components/probe/CSweet.Office.GuestProbe',
        'guest/CSweet.Office.GuestProbe'
    ) | ForEach-Object { Join-Path $linux $_ }
    $linuxExecutables += @(Get-ChildItem "$linux/scripts" -Filter '*.sh' -File -Recurse | ForEach-Object FullName)
    foreach ($executable in $linuxExecutables) {
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "A Linux bundle executable is missing: $executable" }
        & chmod +x $executable
        if ($LASTEXITCODE -ne 0) { throw "Could not make the Linux bundle executable: $executable" }
    }
}
$bundleManifest = Get-Content -LiteralPath (Join-Path $windows 'office-bundle.json') -Raw | ConvertFrom-Json
$bundleManifest | Add-Member -NotePropertyName environmentProfiles -NotePropertyValue @($guestProfile.environmentProfile) -Force
$bundleManifest | Add-Member -NotePropertyName guestBrokerProtocolVersion -NotePropertyValue $guestProfile.guestBrokerProtocolVersion -Force
$bundleManifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $windows 'office-bundle.json') -Encoding utf8
$support = "csweet-office-$Version-windows-support.zip"
$winAsset = "csweet-office-$Version-windows-x64.zip"
$linuxAsset = "csweet-office-$Version-linux-x64.tar.gz"
Compress-Archive -Path "$windows/scripts", "$windows/Directory.Build.props", "$windows/office-bundle.json" -DestinationPath "$OutputRoot/$support"
if ((Get-Item -LiteralPath "$OutputRoot/$support").Length -gt 10MB) { throw 'Office support archive exceeds 10 MiB.' }
Push-Location $windows
try {
    & zip -q -r "$OutputRoot/$winAsset" . -x '*.pdb'
    if ($LASTEXITCODE -ne 0) { throw 'Windows bundle packaging failed.' }
} finally { Pop-Location }
$assetNames = @($support, $winAsset)
if (-not $WindowsOnly) {
    & tar -czf "$OutputRoot/$linuxAsset" -C $linux .
    if ($LASTEXITCODE -ne 0) { throw 'Linux bundle packaging failed.' }
    $assetNames += $linuxAsset
}
$assets = foreach ($name in $assetNames) {
    New-OfficeBundleTransport -ArchivePath "$OutputRoot/$name" -PartSizeBytes $PartSizeBytes `
        -BaseUrl "https://github.com/CrosswiredStudios/CSweet.Office/releases/download/v$Version" -SingleFile:($name -ceq $support)
}
$schemaVersion = if (@($assets | Where-Object { $null -ne $_.PSObject.Properties['parts'] }).Count -gt 0) { 2 } else { 1 }
[ordered]@{ schemaVersion = $schemaVersion; officeVersion = $Version; contractsVersion = $release.ContractsVersion
    protocolVersion = '1.0'; signing = 'development'; requiresHostCertification = $true; assets = @($assets)
    environmentProfiles = @($guestProfile.environmentProfile); guestBrokerProtocolVersion = $guestProfile.guestBrokerProtocolVersion
} | ConvertTo-Json -Depth 6 | Set-Content "$OutputRoot/office-bootstrap.json" -Encoding utf8
if (-not (Get-Content "$OutputRoot/office-bootstrap.json" -Raw | Test-Json -SchemaFile "$PSScriptRoot/../../release/office-bootstrap.schema.json")) {
    throw 'The hosted bootstrap manifest failed schema validation.'
}
Get-ChildItem $OutputRoot -File | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } |
    Set-Content "$OutputRoot/SHA256SUMS" -Encoding ascii
