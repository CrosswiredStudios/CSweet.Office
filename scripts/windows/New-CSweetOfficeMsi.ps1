[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PayloadRoot,
    [Parameter(Mandatory = $true)] [string] $OutputPath,
    [Parameter(Mandatory = $true)] [string] $Version,
    [Parameter(Mandatory = $true)] [string] $CertificateThumbprint,
    [string] $LocalTestSignatureVerifier,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [guid] $UpgradeCode = '8917027b-53f1-4c36-946f-1a704349352e'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use MAJOR.MINOR.PATCH.' }
if ($CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40,128}$') { throw 'CertificateThumbprint must be hexadecimal.' }
$PayloadRoot = [IO.Path]::GetFullPath($PayloadRoot)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $PayloadRoot -PathType Container)) { throw 'PayloadRoot does not exist.' }
if (Test-Path -LiteralPath $OutputPath) { throw "Output already exists: $OutputPath" }
foreach ($required in @('runtime-manifest.json', 'runtime\CSweet.Office.RuntimeHost.exe',
        'node\CSweet.Office.Node.exe', 'configurator\CSweet.Office.Configurator.exe',
        'helper\CSweet.Office.Runtime.HyperV.Helper.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadRoot $required) -PathType Leaf)) {
        throw "Payload is missing $required."
    }
}
$reparsePoint = Get-ChildItem -LiteralPath $PayloadRoot -Recurse -Force |
    Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
    Select-Object -First 1
if ($null -ne $reparsePoint) { throw "Payload may not contain reparse points: $($reparsePoint.FullName)" }

$scriptFiles = @('Initialize-CSweetCombinedOffice.ps1', 'CSweet.OfficeImageParts.ps1', 'CSweet.OfficeStorage.ps1', 'Install-CSweetOffice.ps1', 'Install-CSweetOfficeRuntimeHost.ps1',
    'CSweet.WindowsSetupProgress.ps1', 'Get-CSweetOfficeRecoveryState.ps1', 'Enter-CSweetOfficeMaintenance.ps1',
    'Remove-CSweetOfficeForRecovery.ps1', 'Uninstall-CSweetOffice.ps1',
    'Start-CSweetOfficeAutomaticUpdate.ps1', 'CSweet.OfficeRelease.ps1')
foreach ($file in $scriptFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $file) -PathType Leaf)) {
        throw "Installer source is missing $file."
    }
}

$wixCommand = Get-Command wix.exe, wix -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $wixCommand) { throw 'WiX Toolset v4 (wix) is required.' }
$signTool = Get-Command signtool.exe, signtool -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $signTool) { throw 'Windows SDK signtool is required.' }

$buildRoot = Join-Path ([IO.Path]::GetTempPath()) "csweet-msi-$([guid]::NewGuid().ToString('N'))"
$stageRoot = Join-Path $buildRoot 'stage'
$wxsPath = Join-Path $buildRoot 'csweet-office.wxs'
try {
    New-Item -ItemType Directory -Path $stageRoot, (Split-Path -Parent $OutputPath) -Force | Out-Null
    $publisher = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    [IO.File]::WriteAllBytes((Join-Path $stageRoot 'office-release-signer.cer'), $publisher.RawData)
    foreach ($file in $scriptFiles) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $stageRoot $file)
    }
    Copy-Item -LiteralPath $PayloadRoot -Destination (Join-Path $stageRoot 'payload') -Recurse

    $manifest = Get-Content -Raw -LiteralPath (Join-Path $stageRoot 'payload\runtime-manifest.json') | ConvertFrom-Json
    if ($manifest.guestImage -cne 'images/csweet-agent-guest.vhdx') { throw 'Unexpected Hyper-V guest image path.' }
    $guestImage = Join-Path $stageRoot 'payload\images\csweet-agent-guest.vhdx'
    $images = @($guestImage)
    if ($manifest.PSObject.Properties['computeImageDigest']) {
        $computeImage = Join-Path $stageRoot 'payload\images\csweet-compute-guest.vhdx'
        $hash = Get-FileHash -LiteralPath $computeImage -Algorithm SHA256
        if ($manifest.computeImageDigest -cne ('sha256:' + $hash.Hash.ToLowerInvariant())) { throw 'The compute disk differs from its catalog digest.' }
        $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $stageRoot 'payload\certificates\guest-image-signing.cer'))
        try {
            $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($cert)
            try {
                $hashBytes = [byte[]]@(for ($i = 0; $i -lt 64; $i += 2) { [Convert]::ToByte($hash.Hash.Substring($i, 2), 16) })
                if ($null -eq $key -or $cert.Thumbprint -ine $manifest.guestImageSigningCertificateThumbprint -or
                    !$key.VerifyHash($hashBytes, [IO.File]::ReadAllBytes("$computeImage.sig"),
                        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
                    throw 'The compute disk signature does not match the release guest signer.'
                }
            } finally { if ($key) { $key.Dispose() } }
        } finally { $cert.Dispose() }
        $images += $computeImage
    }
    $splitImage = $false
    foreach ($image in $images) {
        if ((Get-Item -LiteralPath $image).Length -le 1GB) { continue }
        $splitImage = $true
        . (Join-Path $PSScriptRoot 'CSweet.OfficeImageParts.ps1')
        $parts = Split-CSweetOfficeGuestImage -ImagePath $image
        Write-Host "Packaging the guest image in $parts parts."
        Remove-Item -LiteralPath $image
    }
    $cabPrefix = "csweet-office-$Version-"
    if ($splitImage -and (Get-ChildItem -LiteralPath (Split-Path -Parent $OutputPath) -Filter "$cabPrefix*.cab")) {
        throw 'Build the Office MSI in a fresh output directory.'
    }

    function XmlEscape([string] $Value) {
        return [Security.SecurityElement]::Escape($Value)
    }
    function StableId([string] $Prefix, [string] $Value) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($hasher.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
        finally { $hasher.Dispose() }
        return "${Prefix}_$($hash.Substring(0, 24))"
    }

    $componentIds = [Collections.Generic.List[string]]::new()
    $xml = [Text.StringBuilder]::new()
    function AddLine([int] $Indent, [string] $Value) {
        [void]$xml.Append(('  ' * $Indent)).AppendLine($Value)
    }
    function AddDirectoryContents([string] $DirectoryPath, [string] $RelativePath, [int] $Indent) {
        foreach ($file in Get-ChildItem -LiteralPath $DirectoryPath -File | Sort-Object Name) {
            $relativeFile = if ($RelativePath) { "$RelativePath\$($file.Name)" } else { $file.Name }
            $componentId = StableId 'cmp' $relativeFile
            $fileId = StableId 'fil' $relativeFile
            $componentIds.Add($componentId)
            AddLine $Indent "<Component Id=`"$componentId`" Guid=`"*`">"
            if ($splitImage -and $relativeFile -ieq 'payload\images\csweet-agent-guest.vhdx.part0000') {
                AddLine ($Indent + 1) '<RemoveFile Id="RemoveExpandedGuestImage" Name="csweet-agent-guest.vhdx" On="uninstall" />'
            }
            if ($relativeFile -ieq 'payload\images\csweet-compute-guest.vhdx.part0000') {
                AddLine ($Indent + 1) '<RemoveFile Id="RemoveExpandedComputeImage" Name="csweet-compute-guest.vhdx" On="uninstall" />'
            }
            AddLine ($Indent + 1) "<File Id=`"$fileId`" Source=`"$(XmlEscape $file.FullName)`" Name=`"$(XmlEscape $file.Name)`" KeyPath=`"yes`" />"
            AddLine $Indent '</Component>'
        }
        foreach ($directory in Get-ChildItem -LiteralPath $DirectoryPath -Directory | Sort-Object Name) {
            $relativeDirectory = if ($RelativePath) { "$RelativePath\$($directory.Name)" } else { $directory.Name }
            $directoryId = StableId 'dir' $relativeDirectory
            AddLine $Indent "<Directory Id=`"$directoryId`" Name=`"$(XmlEscape $directory.Name)`">"
            AddDirectoryContents $directory.FullName $relativeDirectory ($Indent + 1)
            AddLine $Indent '</Directory>'
        }
    }

    AddLine 0 '<?xml version="1.0" encoding="utf-8"?>'
    AddLine 0 '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">'
    AddLine 1 "<Package Name=`"C-Sweet Office`" Manufacturer=`"C-Sweet`" Version=`"$Version`" UpgradeCode=`"{$($UpgradeCode.ToString().ToUpperInvariant())}`" Scope=`"perMachine`" InstallerVersion=`"500`">"
    AddLine 2 '<MajorUpgrade DowngradeErrorMessage="A newer C-Sweet Office package is already installed." />'
    if ($manifest.PSObject.Properties['computeImageDigest']) {
        $features = @($manifest.computeFeatures)
        if ($manifest.computeImageDigest -cnotmatch '^sha256:[0-9a-f]{64}$' -or $features.Count -gt 64 -or
            $features -cnotcontains 'docker' -or @($features | Where-Object { $_ -isnot [string] -or $_ -cnotmatch '^[a-z0-9][a-z0-9._-]{0,63}$' }).Count) {
            throw 'The compute image declaration is invalid.'
        }
        # The combined release builder reads the immutable Property table after checking the MSI publisher.
        AddLine 2 ('<Property Id="OfficeComputeImageDigest" Value="{0}" />' -f $manifest.computeImageDigest)
        AddLine 2 ('<Property Id="OfficeComputeImageFeatures" Value="{0}" />' -f ($features -join ','))
    }
    if ($splitImage) {
        AddLine 2 "<MediaTemplate CabinetTemplate=`"${cabPrefix}{0}.cab`" MaximumUncompressedMediaSize=`"640`" EmbedCab=`"no`" />"
    } else {
        AddLine 2 '<MediaTemplate EmbedCab="yes" />'
    }
    AddLine 2 '<Property Id="CSWEET_FORCE_REMOVE" Secure="yes" />'
    AddLine 2 '<Property Id="CSWEET_COMBINED_INSTALL" Secure="yes" />'
    AddLine 2 '<CustomAction Id="InitializeCombinedOffice" Directory="System64Folder" Execute="deferred" Impersonate="no" Return="check" HideTarget="yes" ExeCommand="&quot;[System64Folder]WindowsPowerShell\v1.0\powershell.exe&quot; -NoLogo -NoProfile -NonInteractive -File &quot;[ProgramFiles64Folder]CSweet\Office\Initialize-CSweetCombinedOffice.ps1&quot;" />'
    AddLine 2 '<Property Id="INSTALLFOLDER" Secure="yes"><RegistrySearch Id="OfficeInstallLocation" Root="HKLM" Key="Software\CSweet\Office\Storage" Name="InstallRoot" Type="raw" /></Property>'
    AddLine 2 '<CustomAction Id="RemoveExecutionFleet" Directory="INSTALLFOLDER" Execute="deferred" Impersonate="no" Return="check" HideTarget="yes" ExeCommand="&quot;[SystemFolder]WindowsPowerShell\\v1.0\\powershell.exe&quot; -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File &quot;[INSTALLFOLDER]Uninstall-CSweetOffice.ps1&quot; -Elevated" />'
    AddLine 2 '<CustomAction Id="ForceRemoveExecutionFleet" Directory="INSTALLFOLDER" Execute="deferred" Impersonate="no" Return="check" HideTarget="yes" ExeCommand="&quot;[SystemFolder]WindowsPowerShell\\v1.0\\powershell.exe&quot; -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File &quot;[INSTALLFOLDER]Uninstall-CSweetOffice.ps1&quot; -Force -Elevated" />'
    AddLine 2 '<InstallExecuteSequence>'
    AddLine 3 '<Custom Action="InitializeCombinedOffice" After="InstallFiles" Condition="CSWEET_COMBINED_INSTALL=&quot;1&quot; AND NOT Installed AND NOT WIX_UPGRADE_DETECTED" />'
    AddLine 3 '<Custom Action="RemoveExecutionFleet" Before="RemoveFiles" Condition="REMOVE=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE AND CSWEET_FORCE_REMOVE&lt;&gt;&quot;1&quot;" />'
    AddLine 3 '<Custom Action="ForceRemoveExecutionFleet" Before="RemoveFiles" Condition="REMOVE=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE AND CSWEET_FORCE_REMOVE=&quot;1&quot;" />'
    AddLine 2 '</InstallExecuteSequence>'
    AddLine 2 '<StandardDirectory Id="ProgramFiles64Folder">'
    AddLine 3 '<Directory Id="CSweetVendorFolder" Name="CSweet">'
    AddLine 4 '<Directory Id="INSTALLFOLDER" Name="Office">'
    AddDirectoryContents $stageRoot '' 5
    $protocolComponentId = 'cmp_csweet_office_protocol'
    AddLine 5 "<Component Id=`"$protocolComponentId`" Guid=`"*`">"
    AddLine 6 '<RegistryKey Root="HKLM" Key="Software\CSweet\Office">'
    AddLine 7 '<RegistryValue Name="ProductCode" Type="string" Value="[ProductCode]" />'
    AddLine 6 '</RegistryKey>'
    AddLine 6 '<RegistryKey Root="HKLM" Key="Software\Classes\csweet-office">'
    AddLine 7 '<RegistryValue Type="string" Value="URL:C-Sweet Office Protocol" KeyPath="yes" />'
    AddLine 7 '<RegistryValue Name="URL Protocol" Type="string" Value="" />'
    AddLine 7 '<RegistryKey Key="shell\open\command">'
    AddLine 8 '<RegistryValue Type="string" Value="&quot;[INSTALLFOLDER]payload\configurator\CSweet.Office.Configurator.exe&quot; &quot;%1&quot;" />'
    AddLine 7 '</RegistryKey>'
    AddLine 6 '</RegistryKey>'
    AddLine 5 '</Component>'
    AddLine 4 '</Directory>'
    AddLine 3 '</Directory>'
    AddLine 2 '</StandardDirectory>'
    AddLine 2 '<Feature Id="MainFeature" Title="C-Sweet Office" Level="1">'
    AddLine 3 "<ComponentRef Id=`"$protocolComponentId`" />"
    foreach ($componentId in $componentIds) { AddLine 3 "<ComponentRef Id=`"$componentId`" />" }
    AddLine 2 '</Feature>'
    AddLine 1 '</Package>'
    AddLine 0 '</Wix>'
    [IO.File]::WriteAllText($wxsPath, $xml.ToString(), [Text.UTF8Encoding]::new($false))

    & $wixCommand.Source build -arch x64 -o $OutputPath $wxsPath
    if ($LASTEXITCODE -ne 0) { throw 'WiX failed to build the MSI.' }
    $signArgs = @('sign', '/sha1', $CertificateThumbprint, '/fd', 'SHA256')
    if ($LocalTestSignatureVerifier) {
        $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
        if ($cert.Subject -ne $cert.Issuer) { throw 'Local test signing requires a self-signed test certificate.' }
        $pem = Join-Path $buildRoot 'test-signer.pem'
        $encoded = [Convert]::ToBase64String($cert.RawData, [Base64FormattingOptions]::InsertLineBreaks)
        [IO.File]::WriteAllText($pem, "-----BEGIN CERTIFICATE-----`n$encoded`n-----END CERTIFICATE-----`n")
    } else { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    function Sign-OfficeFile([string] $Path) {
        & $signTool.Source @signArgs $Path
        if ($LASTEXITCODE -ne 0) { throw "Could not sign $Path" }
        if ($LocalTestSignatureVerifier) {
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try { $pin = [BitConverter]::ToString($sha256.ComputeHash($cert.RawData)).Replace('-', '') }
            finally { $sha256.Dispose() }
            & $LocalTestSignatureVerifier verify -CAfile $pem -require-leaf-hash "sha256:$pin" -in $Path
        } else { & $signTool.Source verify /pa /all $Path }
        if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $Path" }
    }
    if ($splitImage) {
        $cabs = @(Get-ChildItem -LiteralPath (Split-Path -Parent $OutputPath) -Filter "$cabPrefix*.cab" -File)
        if ($cabs.Count -eq 0) { throw 'WiX did not produce the Office cabinets.' }
        foreach ($cab in $cabs) { Sign-OfficeFile $cab.FullName }
        $inscribed = Join-Path $buildRoot 'inscribed.msi'
        & $wixCommand.Source msi inscribe $OutputPath -out $inscribed
        if ($LASTEXITCODE -ne 0) { throw 'Failed to bind the signed cabinets to the Office MSI.' }
        Copy-Item -LiteralPath $inscribed -Destination $OutputPath -Force
    }
    Sign-OfficeFile $OutputPath
    Write-Host "Created signed Windows installer at $OutputPath"
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($buildRoot)) -eq $tempRoot -and
        [IO.Path]::GetFileName($buildRoot) -match '^csweet-msi-[a-f0-9]{32}$' -and (Test-Path -LiteralPath $buildRoot)) {
        Remove-Item -LiteralPath $buildRoot -Recurse -Force
    }
}
