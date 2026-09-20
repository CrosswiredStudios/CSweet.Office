$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Join-Path ([IO.Path]::GetTempPath()) "office-manifest-test-$([guid]::NewGuid().ToString('N'))"
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$version = ([xml](Get-Content "$repository/Directory.Build.props" -Raw)).Project.PropertyGroup.VersionPrefix
try {
    foreach ($relative in @('binaries-win-x64/scripts/windows/setup.ps1',
        'binaries-win-x64/Directory.Build.props', 'binaries-win-x64/office-bundle.json',
        'binaries-linux-x64/components/node/CSweet.Office.Node', 'binaries-linux-x64/scripts/linux/setup.sh',
        'guest-images/windows-guest/csweet-agent-guest.vhdx', 'guest-images/linux-guest/csweet-agent-guest.ext4',
        'guest-images/linux-tools/firecracker', 'guest-images/linux-tools/jailer')) {
        $path = Join-Path "$root/input" $relative
        New-Item -ItemType Directory (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, 'test fixture only')
    }
    if ($env:OS -eq 'Windows_NT') {
        # Exercise the same packaging script on Windows without installing Unix zip/chmod.
        function chmod { $global:LASTEXITCODE = 0 }
        function zip {
            [IO.Compression.ZipFile]::CreateFromDirectory((Get-Location).Path, $args[2])
            $global:LASTEXITCODE = 0
        }
    }
    & "$repository/scripts/release/Publish-HostedOfficeAssets.ps1" -Version $version -InputRoot "$root/input" -OutputRoot "$root/output"
    $manifest = Get-Content "$root/output/office-bootstrap.json" -Raw | ConvertFrom-Json
    if ($manifest.officeVersion -cne $version -or $manifest.assets.Count -ne 3) { throw 'Release identity or assets are wrong.' }
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
    } finally { $zip.Dispose() }
    Write-Host 'Hosted archive layout, manifest schema, sizes, hashes, and immutable URLs passed.'
} finally {
    if ([IO.Path]::GetFullPath($root).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $root)) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
