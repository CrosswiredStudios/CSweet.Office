param([string] $InstallerScript)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/Test-OfficeBundleTransport.ps1"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Join-Path ([IO.Path]::GetTempPath()) "office-manifest-test-$([guid]::NewGuid().ToString('N'))"
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$version = ([xml](Get-Content "$repository/Directory.Build.props" -Raw)).Project.PropertyGroup.VersionPrefix
try {
    foreach ($relative in @('binaries-win-x64/scripts/windows/setup.ps1',
        'binaries-win-x64/Directory.Build.props', 'binaries-win-x64/office-bundle.json',
        'binaries-linux-x64/components/node/CSweet.Office.Node',
        'binaries-linux-x64/components/runtime/CSweet.Office.RuntimeHost',
        'binaries-linux-x64/components/runtime/appsettings.json',
        'binaries-linux-x64/components/helper/CSweet.Office.Runtime.Firecracker.Helper',
        'binaries-linux-x64/components/smoke/CSweet.Office.WindowsSmokeTest',
        'binaries-linux-x64/components/probe/CSweet.Office.GuestProbe', 'binaries-linux-x64/scripts/linux/setup.sh',
        'guest-images/linux-guest/CSweet.Office.GuestProbe',
        'guest-images/windows-guest/csweet-agent-guest.vhdx', 'guest-images/linux-guest/csweet-agent-guest.ext4',
        'guest-images/linux-tools/firecracker', 'guest-images/linux-tools/jailer')) {
        $path = Join-Path "$root/input" $relative
        New-Item -ItemType Directory (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, 'test fixture only')
        if ($env:OS -ne 'Windows_NT') {
            & chmod 0644 $path # Match actions/download-artifact's restored file modes.
            if ($LASTEXITCODE -ne 0) { throw 'Could not initialize artifact fixture permissions.' }
        }
    }
    [ordered]@{ schemaVersion = 1; officeVersion = $version; contractsVersion = '0.10.0'; runtimeIdentifier = 'win-x64';
        signing = 'development'; requiresHostCertification = $true } | ConvertTo-Json |
        Set-Content "$root/input/binaries-win-x64/office-bundle.json" -Encoding utf8
    [ordered]@{ environmentProfile = 'software-development-polyglot-v1'; guestBrokerProtocolVersion = '1.1';
        guestImageSha256 = (Get-FileHash "$root/input/guest-images/windows-guest/csweet-agent-guest.vhdx" -Algorithm SHA256).Hash.ToLowerInvariant() } |
        ConvertTo-Json | Set-Content "$root/input/guest-images/windows-guest/guest-image-profile.json" -Encoding utf8
    if ($env:OS -eq 'Windows_NT') {
        # Exercise the same packaging script on Windows without installing Unix zip/chmod.
        function chmod { $global:LASTEXITCODE = 0 }
        function zip {
            [IO.Compression.ZipFile]::CreateFromDirectory((Get-Location).Path, $args[2])
            $global:LASTEXITCODE = 0
        }
    }
    $windowsInput = "$root/windows-only-input"
    New-Item -ItemType Directory $windowsInput | Out-Null
    Copy-Item "$root/input/binaries-win-x64" "$windowsInput/binaries-win-x64" -Recurse
    Copy-Item "$root/input/guest-images/windows-guest" "$windowsInput/windows-guest" -Recurse
    $profilePath = "$windowsInput/windows-guest/guest-image-profile.json"
    $profile = Get-Content -LiteralPath $profilePath -Raw
    $profileBackup = "$profilePath.saved"
    Move-Item -LiteralPath $profilePath -Destination $profileBackup
    $rejected = $false
    try { & "$repository/scripts/release/Publish-HostedOfficeAssets.ps1" -Version $version -InputRoot $windowsInput -OutputRoot "$root/reject-missing" -WindowsOnly }
    catch { $rejected = $_.Exception.Message -like '*no development-profile build evidence*' }
    if (-not $rejected) { throw 'Packaging admitted an image without development-profile evidence.' }
    Move-Item -LiteralPath $profileBackup -Destination $profilePath
    $tampered = $profile | ConvertFrom-Json
    $tampered.guestImageSha256 = '0' * 64
    $tampered | ConvertTo-Json | Set-Content -LiteralPath $profilePath -Encoding utf8
    $rejected = $false
    try { & "$repository/scripts/release/Publish-HostedOfficeAssets.ps1" -Version $version -InputRoot $windowsInput -OutputRoot "$root/reject-hash" -WindowsOnly }
    catch { $rejected = $_.Exception.Message -like '*does not match its development-profile build evidence*' }
    if (-not $rejected) { throw 'Packaging admitted a substituted image under another profile declaration.' }
    Set-Content -LiteralPath $profilePath -Value $profile -Encoding utf8
    & "$repository/scripts/release/Publish-HostedOfficeAssets.ps1" -Version $version -InputRoot "$root/input" -OutputRoot "$root/output"
    $manifest = Get-Content "$root/output/office-bootstrap.json" -Raw | ConvertFrom-Json
    if ($manifest.officeVersion -cne $version -or $manifest.assets.Count -ne 3) { throw 'Release identity or assets are wrong.' }
    if ($manifest.guestBrokerProtocolVersion -cne '1.1' -or
        @($manifest.environmentProfiles) -cnotcontains 'software-development-polyglot-v1') { throw 'The published guest profile is missing.' }
    foreach ($asset in $manifest.assets) {
        $file = Get-Item "$root/output/$($asset.name)"
        if ($file.Length -ne $asset.size -or (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.sha256) {
            throw "Incorrect asset metadata: $($asset.name)"
        }
        if ($asset.url -cne "https://github.com/CrosswiredStudios/CSweet.Office/releases/download/v$version/$($asset.name)") { throw 'Asset URL is not pinned to its tag.' }
    }
    $zip = [IO.Compression.ZipFile]::OpenRead("$root/output/csweet-office-$version-windows-x64.zip")
    try {
        if ($null -eq $zip.GetEntry('guest/csweet-agent-guest.vhdx')) { throw 'Windows image is missing from the archive.' }
        $reader = [IO.StreamReader]::new($zip.GetEntry('office-bundle.json').Open())
        try { $bundle = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($bundle.guestBrokerProtocolVersion -cne '1.1' -or
            @($bundle.environmentProfiles) -cnotcontains 'software-development-polyglot-v1') { throw 'The archive lost its guest profile.' }
    } finally { $zip.Dispose() }
    if ($env:OS -ne 'Windows_NT') {
        $extracted = Join-Path $root 'extracted-linux'
        New-Item -ItemType Directory $extracted | Out-Null
        & tar -xzf "$root/output/csweet-office-$version-linux-x64.tar.gz" -C $extracted
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract the Linux release archive.' }
        $executeBits = [IO.UnixFileMode]::UserExecute -bor [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherExecute
        foreach ($relative in @('components/node/CSweet.Office.Node', 'components/runtime/CSweet.Office.RuntimeHost',
            'components/helper/CSweet.Office.Runtime.Firecracker.Helper', 'components/smoke/CSweet.Office.WindowsSmokeTest',
            'components/probe/CSweet.Office.GuestProbe', 'guest/CSweet.Office.GuestProbe',
            'tools/firecracker', 'tools/jailer', 'scripts/linux/setup.sh')) {
            $mode = [IO.File]::GetUnixFileMode((Join-Path $extracted $relative))
            if (($mode -band $executeBits) -ne $executeBits) { throw "Linux archive executable permissions were lost: $relative" }
        }
        $dataMode = [IO.File]::GetUnixFileMode((Join-Path $extracted 'components/runtime/appsettings.json'))
        if (($dataMode -band $executeBits) -ne 0) { throw 'Linux data files must not be marked executable.' }
        Write-Host 'Extracted Linux executable and data file modes passed.'
    }
    & "$repository/scripts/release/Publish-HostedOfficeAssets.ps1" -Version $version -InputRoot $windowsInput -OutputRoot "$root/windows-only-output" -WindowsOnly
    $windowsManifest = Get-Content "$root/windows-only-output/office-bootstrap.json" -Raw | ConvertFrom-Json
    if ($windowsManifest.assets.Count -ne 2 -or $windowsManifest.requiresHostCertification -ne $true -or $windowsManifest.signing -cne 'development') {
        throw 'Windows-only assets or certification requirements are wrong.'
    }
    foreach ($asset in $windowsManifest.assets) {
        $file = Get-Item "$root/windows-only-output/$($asset.name)"
        if ($asset.name -match 'linux' -or $file.Length -ne $asset.size -or (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.sha256) {
            throw 'Windows-only asset metadata is wrong.'
        }
    }
    $windowsZip = [IO.Compression.ZipFile]::OpenRead("$root/windows-only-output/csweet-office-$version-windows-x64.zip")
    try {
        if ($null -eq $windowsZip.GetEntry('guest/csweet-agent-guest.vhdx')) { throw 'Windows-only image is missing.' }
    } finally { $windowsZip.Dispose() }
    $schema = "$repository/release/office-bootstrap.schema.json"
    $windowsManifest.assets = @($windowsManifest.assets[0], $windowsManifest.assets[0])
    if ($windowsManifest | ConvertTo-Json -Depth 6 | Test-Json -SchemaFile $schema -ErrorAction SilentlyContinue) {
        throw 'Duplicate support assets cannot replace the required Windows bundle.'
    }
    # Force real publisher multipart output using a small random image and part boundary.
    $multipartInput = "$root/multipart-input"
    $multipartOutput = "$root/multipart-output"
    New-Item -ItemType Directory "$multipartInput/binaries-win-x64" -Force | Out-Null
    foreach ($name in @('scripts', 'Directory.Build.props', 'office-bundle.json')) {
        Copy-Item -LiteralPath "$windowsInput/binaries-win-x64/$name" -Destination "$multipartInput/binaries-win-x64/$name" -Recurse
    }
    Copy-Item -LiteralPath "$windowsInput/windows-guest" -Destination "$multipartInput/windows-guest" -Recurse
    $imagePath = "$multipartInput/windows-guest/csweet-agent-guest.vhdx"
    $image = [byte[]]::new(256KB); [Random]::new(42).NextBytes($image); [IO.File]::WriteAllBytes($imagePath, $image)
    $profile = Get-Content -LiteralPath "$multipartInput/windows-guest/guest-image-profile.json" -Raw | ConvertFrom-Json
    $profile.guestImageSha256 = (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $profile | ConvertTo-Json | Set-Content -LiteralPath "$multipartInput/windows-guest/guest-image-profile.json" -Encoding utf8
    & "$repository/scripts/release/Publish-HostedOfficeAssets.ps1" -Version $version -InputRoot $multipartInput -OutputRoot $multipartOutput -WindowsOnly -PartSizeBytes 64KB
    $multipartManifest = Get-Content -LiteralPath "$multipartOutput/office-bootstrap.json" -Raw | ConvertFrom-Json
    $bundle = $multipartManifest.assets | Where-Object name -CEQ "csweet-office-$version-windows-x64.zip"
    if ($multipartManifest.schemaVersion -ne 2 -or $bundle.parts.Count -lt 2 -or
        (Test-Path -LiteralPath "$multipartOutput/$($bundle.name)")) { throw 'Multipart publication did not replace the oversized logical archive.' }
    $joined = "$root/reconstructed.zip"
    $output = [IO.File]::Create($joined)
    try {
        for ($index = 0; $index -lt $bundle.parts.Count; $index++) {
            $part = $bundle.parts[$index]
            $expectedName = '{0}.part{1:D3}' -f $bundle.name, ($index + 1)
            $file = Get-Item -LiteralPath "$multipartOutput/$($part.name)"
            if ($part.name -cne $expectedName -or $part.size -gt 64KB -or $file.Length -ne $part.size -or
                (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $part.sha256) { throw 'Published part metadata differs from the bytes.' }
            $input = [IO.File]::OpenRead($file.FullName)
            try { $input.CopyTo($output) } finally { $input.Dispose() }
        }
    } finally { $output.Dispose() }
    if ((Get-Item -LiteralPath $joined).Length -ne $bundle.size -or
        (Get-FileHash -LiteralPath $joined -Algorithm SHA256).Hash.ToLowerInvariant() -cne $bundle.sha256) { throw 'Published archive digest did not survive reconstruction.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($joined)
    try { if ($zip.GetEntry('guest/csweet-agent-guest.vhdx').Length -ne $image.Length) { throw 'Multipart bundle lost the guest image.' } }
    finally { $zip.Dispose() }
    foreach ($file in Get-ChildItem -LiteralPath $multipartOutput -File | Where-Object Name -NE 'SHA256SUMS') {
        $line = "$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($file.Name)"
        if ($line -cnotin @(Get-Content -LiteralPath "$multipartOutput/SHA256SUMS")) { throw 'Physical release asset is missing from SHA256SUMS.' }
    }
    $invalid = $multipartManifest | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $invalid.schemaVersion = 1
    if ($invalid | ConvertTo-Json -Depth 8 | Test-Json -SchemaFile $schema -ErrorAction SilentlyContinue) { throw 'Schema 1 admitted multipart assets.' }
    $invalid.schemaVersion = 2
    $invalid.assets[1].parts[0].size = 1GB + 1L
    if ($invalid | ConvertTo-Json -Depth 8 | Test-Json -SchemaFile $schema -ErrorAction SilentlyContinue) { throw 'Schema admitted a part larger than 1 GiB.' }
    if ($InstallerScript) {
        . ([IO.Path]::GetFullPath($InstallerScript))
        $script:publishedFixture = $multipartOutput
        $script:publishedManifest = $multipartManifest
        $script:publishedVersion = $version
        function Get-CSweetReleaseJson([string] $Url) {
            if ($Url.Contains('api.github.com')) { return @([pscustomobject]@{ tag_name = "v$script:publishedVersion"; draft = $false; prerelease = $false; assets = @([pscustomobject]@{ name = 'office-bootstrap.json' }) }) }
            return $script:publishedManifest
        }
        function Invoke-CSweetReleaseDownload {
            param($Uri, $OutputPath, $MaximumBytes, $TimeoutSeconds)
            $file = Join-Path $script:publishedFixture ([uri]$Uri).Segments[-1]
            if ((Get-Item -LiteralPath $file).Length -gt $MaximumBytes) { throw 'Fixture exceeded the authenticated download bound.' }
            Copy-Item -LiteralPath $file -Destination $OutputPath
        }
        $release = Resolve-CSweetOfficeRelease 'x64'
        Receive-CSweetOfficeAsset $release.Support "$root/installed-support" | Out-Null
        Receive-CSweetOfficeAsset $release.Bundle "$root/installed-bundle" | Out-Null
        if ((Get-FileHash -LiteralPath "$root/installed-bundle/guest/csweet-agent-guest.vhdx" -Algorithm SHA256).Hash.ToLowerInvariant() -cne $profile.guestImageSha256) { throw 'The real installer did not reconstruct the real publisher image.' }
        Write-Host 'Cross-repository publisher -> discovery -> verification -> extraction passed.'
    }
    Write-Host 'Multipart publisher, schema, SHA256SUMS and reconstructed image checks passed.'
    Write-Host 'Combined and Windows-only archive layouts, manifest schema, sizes, hashes, and immutable URLs passed.'
} finally {
    if ([IO.Path]::GetFullPath($root).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $root)) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
