$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../release/OfficeBundleTransport.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "office-transport-test-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null
$checks = 0
try {
    foreach ($size in @(1, 1023, 1024, 1025, 2048, 2457, 16384)) {
        $bytes = [byte[]]::new($size); [Random]::new(42).NextBytes($bytes)
        $path = Join-Path $testRoot "fixture-$size.zip"
        [IO.File]::WriteAllBytes($path, $bytes)
        $expected = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $asset = New-OfficeBundleTransport -ArchivePath $path -BaseUrl 'https://github.com/fixture' -PartSizeBytes 1024
        if ($size -le 1024) {
            if (-not (Test-Path -LiteralPath $path) -or $asset.sha256 -cne $expected -or $asset.size -ne $size -or
                $null -ne $asset.PSObject.Properties['parts']) { throw 'Small bundle transport changed.' }
        } else {
            if ((Test-Path -LiteralPath $path) -or $asset.parts.Count -ne [Math]::Ceiling($size / 1024.0)) { throw 'Multipart threshold or count is wrong.' }
            $joined = Join-Path $testRoot "joined-$size.zip"
            $output = [IO.File]::Create($joined)
            try {
                foreach ($part in $asset.parts) {
                    $partPath = Join-Path $testRoot $part.name
                    if ($part.size -gt 1024 -or (Get-Item -LiteralPath $partPath).Length -ne $part.size -or
                        (Get-FileHash -LiteralPath $partPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $part.sha256) { throw 'Transport part size or digest is wrong.' }
                    $input = [IO.File]::OpenRead($partPath)
                    try { $input.CopyTo($output) } finally { $input.Dispose() }
                }
            } finally { $output.Dispose() }
            if ((Get-Item -LiteralPath $joined).Length -ne $size -or
                (Get-FileHash -LiteralPath $joined -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected -or $asset.sha256 -cne $expected) { throw 'Reconstructed archive differs.' }
        }
        $checks++
    }
    $path = Join-Path $testRoot 'excess.zip'
    [IO.File]::WriteAllBytes($path, [byte[]]::new(16385))
    $rejected = $false
    try { New-OfficeBundleTransport -ArchivePath $path -BaseUrl 'https://github.com/fixture' -PartSizeBytes 1024 | Out-Null } catch { $rejected = $true }
    if (-not $rejected -or -not (Test-Path -LiteralPath $path)) { throw 'Part-count limit must fail before removing the original archive.' }
    $checks++
    Write-Host "Passed $checks bundle transport boundary checks."
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
