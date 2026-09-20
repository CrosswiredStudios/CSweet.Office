param([Parameter(Mandatory)][string] $Version, [Parameter(Mandatory)][string] $InputRoot,
    [Parameter(Mandatory)][string] $OutputRoot)
$ErrorActionPreference = 'Stop'
$release = & "$PSScriptRoot/Get-OfficeReleaseMetadata.ps1" -Version $Version
$InputRoot = [IO.Path]::GetFullPath($InputRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'Release packaging requires a new output directory.' }
New-Item -ItemType Directory $OutputRoot -Force | Out-Null
$windows = "$InputRoot/binaries-win-x64"
$linux = "$InputRoot/binaries-linux-x64"
$images = "$InputRoot/guest-images"
Copy-Item "$images/windows-guest" "$windows/guest" -Recurse
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
$support = "csweet-office-$Version-windows-support.zip"
$winAsset = "csweet-office-$Version-windows-x64.zip"
$linuxAsset = "csweet-office-$Version-linux-x64.tar.gz"
Compress-Archive -Path "$windows/scripts", "$windows/Directory.Build.props", "$windows/office-bundle.json" -DestinationPath "$OutputRoot/$support"
Push-Location $windows
try {
    & zip -q -r "$OutputRoot/$winAsset" . -x '*.pdb'
    if ($LASTEXITCODE -ne 0) { throw 'Windows bundle packaging failed.' }
} finally { Pop-Location }
& tar -czf "$OutputRoot/$linuxAsset" -C $linux .
if ($LASTEXITCODE -ne 0) { throw 'Linux bundle packaging failed.' }
$assets = foreach ($name in @($support, $winAsset, $linuxAsset)) {
    $file = Get-Item "$OutputRoot/$name"
    if ($file.Length -ge 2GB) { throw "GitHub release asset exceeds 2 GiB: $name" }
    [ordered]@{ name = $name; size = $file.Length; sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        url = "https://github.com/CrosswiredStudios/CSweet.Office/releases/download/v$Version/$name" }
}
[ordered]@{ schemaVersion = 1; officeVersion = $Version; contractsVersion = $release.ContractsVersion
    protocolVersion = '1.0'; signing = 'development'; requiresHostCertification = $true; assets = @($assets)
} | ConvertTo-Json -Depth 6 | Set-Content "$OutputRoot/office-bootstrap.json" -Encoding utf8
if (-not (Get-Content "$OutputRoot/office-bootstrap.json" -Raw | Test-Json -SchemaFile "$PSScriptRoot/../../release/office-bootstrap.schema.json")) {
    throw 'The hosted bootstrap manifest failed schema validation.'
}
Get-ChildItem $OutputRoot -File | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } |
    Set-Content "$OutputRoot/SHA256SUMS" -Encoding ascii
