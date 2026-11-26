param([Parameter(Mandatory)][string] $Version, [Parameter(Mandatory)][string] $InputRoot,
    [Parameter(Mandatory)][string] $OutputRoot, [switch] $WindowsOnly,
    [ValidateRange(1, 1073741824)][long] $PartSizeBytes = 1GB,
    [Security.Cryptography.X509Certificates.X509Certificate2] $UpdatePublisher)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/OfficeBundleTransport.ps1"
$release = & "$PSScriptRoot/Get-OfficeReleaseMetadata.ps1" -Version $Version
$InputRoot = [IO.Path]::GetFullPath($InputRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'Release packaging requires a new output directory.' }
New-Item -ItemType Directory $OutputRoot -Force | Out-Null
$windows = "$InputRoot/binaries-win-x64"
if ($UpdatePublisher) {
    if (!$UpdatePublisher.HasPrivateKey -or $UpdatePublisher.NotBefore -gt (Get-Date) -or $UpdatePublisher.NotAfter -le (Get-Date)) {
        throw 'The development update publisher must have a current signing key.'
    }
    [IO.File]::WriteAllBytes((Join-Path $windows 'scripts/windows/office-release-signer.cer'), $UpdatePublisher.RawData)
}
$linux = "$InputRoot/binaries-linux-x64"
$images = "$InputRoot/guest-images"
$guestInput = if ($WindowsOnly) { "$InputRoot/windows-guest" } else { "$images/windows-guest" }
$computeProfile = Get-Content -Raw -LiteralPath (Join-Path $guestInput 'compute-image-profile.json') | ConvertFrom-Json
$computeHash = (Get-FileHash -LiteralPath (Join-Path $guestInput 'csweet-compute-guest.vhdx') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($computeProfile.imageDigest -cne "sha256:$computeHash" -or
    (@($computeProfile.features) -join ',') -cnotin @('docker,docker-apps-v1', 'docker,docker-apps-v1,browser-testing-v1')) {
    throw 'The compute disk does not match its release profile.'
}
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
$bundleManifest | Add-Member -NotePropertyName computeImage -NotePropertyValue $computeProfile -Force
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
$archive = [IO.Compression.ZipFile]::OpenRead("$OutputRoot/$winAsset")
try {
    $expandedBytes = 0L
    foreach ($entry in $archive.Entries) { $expandedBytes += $entry.Length }
    if ($archive.Entries.Count -gt 10000 -or $expandedBytes -gt 16GB) {
        throw 'The Windows payload exceeds the installed release reader limit of 10000 entries or 16 GiB expanded.'
    }
} finally { $archive.Dispose() }
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
    computeImage = $computeProfile
} | ConvertTo-Json -Depth 6 | Set-Content "$OutputRoot/office-bootstrap.json" -Encoding utf8
if (-not (Get-Content "$OutputRoot/office-bootstrap.json" -Raw | Test-Json -SchemaFile "$PSScriptRoot/../../release/office-bootstrap.schema.json")) {
    throw 'The hosted bootstrap manifest failed schema validation.'
}
if ($UpdatePublisher) {
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    $content = [Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes("$OutputRoot/office-bootstrap.json"))
    $signed = [Security.Cryptography.Pkcs.SignedCms]::new($content, $false)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($UpdatePublisher)
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $signed.ComputeSignature($signer)
    $signed.CheckSignature($true)
    [IO.File]::WriteAllText("$OutputRoot/office-bootstrap.p7m.b64", [Convert]::ToBase64String($signed.Encode()))
}
Get-ChildItem $OutputRoot -File | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } |
    Set-Content "$OutputRoot/SHA256SUMS" -Encoding ascii
