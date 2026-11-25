[CmdletBinding()]
param([Parameter(Mandatory)][string] $Version, [Parameter(Mandatory)][int] $Revision,
    [Parameter(Mandatory)][string] $PublisherSha256, [Parameter(Mandatory)][string] $Wix,
    [Parameter(Mandatory)][string] $Output)
$ErrorActionPreference = 'Stop'
if ($Version -cnotmatch '^\d+\.\d+\.\d+$' -or $Revision -notin 1..65535 -or $PublisherSha256 -cnotmatch '^[a-f0-9]{64}$') {
    throw 'Invalid Windows test release version, revision or publisher pin.'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$Output = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory($Output) | Out-Null
$inputs = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ("office-test-build-" + [guid]::NewGuid().ToString('N')))).FullName
$build = "$Version-$Revision"
$name = "csweet-office-$build-windows-x64-test"
try {
    Push-Location $inputs
    try {
        Copy-Item "$PSScriptRoot/WindowsTestSetup.wxs","$PSScriptRoot/Open-WindowsTestOffice.ps1","$PSScriptRoot/Prepare-WindowsTestOffice.ps1" .
        Copy-Item "$repo/scripts/windows/CSweet.OfficeRelease.ps1","$repo/scripts/windows/CSweet.OfficeStorage.ps1" .
        . ./CSweet.OfficeRelease.ps1
        $base = "https://github.com/CrosswiredStudios/CSweet.Office/releases/download/v$Version"
        Invoke-CSweetReleaseDownload -Uri "$base/office-bootstrap.p7m.b64" -OutputPath "$inputs/office-bootstrap.p7m.b64" -MaximumBytes 2MB
        $envelope = [IO.File]::ReadAllText("$inputs/office-bootstrap.p7m.b64")
        Add-Type -AssemblyName System.Security
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode([Convert]::FromBase64String($envelope))
        if ($cms.SignerInfos.Count -ne 1) { throw 'Expected one development publisher.' }
        [IO.File]::WriteAllBytes("$inputs/office-release-signer.cer", $cms.SignerInfos[0].Certificate.RawData)
        if ((Get-FileHash office-release-signer.cer).Hash -ine $PublisherSha256) { throw 'Development publisher does not match the repository pin.' }
        $manifest = Read-CSweetSignedReleaseManifest $envelope "$inputs/office-release-signer.cer"
        if ($manifest.officeVersion -cne $Version -or $manifest.signing -cne 'development' -or $manifest.requiresHostCertification -ne $true) { throw 'Unexpected development manifest.' }
        $bundle = @($manifest.assets | Where-Object name -CEQ "csweet-office-$Version-windows-x64.zip")
        if ($bundle.Count -ne 1) { throw 'Missing Windows x64 release bundle.' }
        Assert-CSweetOfficeAsset $bundle[0] $Version 'windows-x64' $true
        [IO.File]::WriteAllText("$inputs/test-build.txt", "windows-test-$build")
        $v = [version]$Version
        & $Wix build -arch x64 -d "Version=$($v.Major).$($v.Minor).$Revision" -o "$Output/$name.msi" WindowsTestSetup.wxs
        if ($LASTEXITCODE -ne 0) { throw 'Failed to build Windows test MSI.' }
        $msi = Get-Item "$Output/$name.msi"
        [IO.File]::WriteAllLines("$inputs/assets.txt", @($build, "$($msi.Name)|$($msi.Length)|$((Get-FileHash $msi.FullName).Hash)"))
        $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
        & $compiler /nologo /target:winexe /platform:x64 /optimize+ /define:WINDOWS_TEST_INSTALLER /reference:System.Net.Http.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/resource:$inputs/assets.txt,OfficeFiles" "/out:$Output/$name-setup.exe" "$repo/scripts/windows/OfficeSetup.cs"
        if ($LASTEXITCODE -ne 0) { throw 'Failed to build Windows test Setup EXE.' }
        $sums = Get-ChildItem -Path "$Output/*.msi","$Output/*.exe" | ForEach-Object { "$((Get-FileHash $_.FullName).Hash.ToLowerInvariant())  $($_.Name)" }
        [IO.File]::WriteAllLines("$Output/SHA256SUMS", [string[]]$sums)
    } finally { Pop-Location }
} finally {
    if ([IO.Path]::GetDirectoryName($inputs) -eq [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -and
        [IO.Path]::GetFileName($inputs) -match '^office-test-build-[a-f0-9]{32}$') { Remove-Item -LiteralPath $inputs -Recurse -Force }
}
