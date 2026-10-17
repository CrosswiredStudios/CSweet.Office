# Release transport only; the logical archive and guest digests remain unchanged.
Set-StrictMode -Version Latest

function New-OfficeBundleTransport {
    param([Parameter(Mandatory)][string] $ArchivePath,
        [Parameter(Mandatory)][string] $BaseUrl,
        [ValidateRange(1, 1073741824)][long] $PartSizeBytes = 1GB,
        [switch] $SingleFile)
    $archive = Get-Item -LiteralPath $ArchivePath
    if ($archive.Length -le 0 -or $archive.Length -gt 16GB) { throw 'Office bundle exceeds the 16 GiB transport limit.' }
    $digest = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset = [ordered]@{ name = $archive.Name; size = $archive.Length; sha256 = $digest }
    if ($SingleFile -or $archive.Length -le $PartSizeBytes) {
        if ($archive.Length -ge 2GB) { throw "GitHub release asset exceeds 2 GiB: $($archive.Name)" }
        $asset.url = "$BaseUrl/$($archive.Name)"
        return [pscustomobject]$asset
    }
    $partCount = [int][Math]::Ceiling($archive.Length / [double]$PartSizeBytes)
    if ($partCount -gt 16) { throw 'Office bundle requires more than 16 transport parts.' }
    $parts = @()
    $input = [IO.File]::OpenRead($archive.FullName)
    try {
        $buffer = [byte[]]::new(1MB)
        for ($index = 1; $index -le $partCount; $index++) {
            $name = '{0}.part{1:D3}' -f $archive.Name, $index
            $path = Join-Path $archive.DirectoryName $name
            $output = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $remaining = [Math]::Min($PartSizeBytes, $archive.Length - $input.Position)
                while ($remaining -gt 0) {
                    $read = $input.Read($buffer, 0, [int][Math]::Min($buffer.Length, $remaining))
                    if ($read -eq 0) { throw 'Office archive ended before all transport parts were written.' }
                    $output.Write($buffer, 0, $read)
                    $remaining -= $read
                }
            } finally { $output.Dispose() }
            $parts += [pscustomobject][ordered]@{ name = $name; size = (Get-Item -LiteralPath $path).Length
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); url = "$BaseUrl/$name" }
        }
    } finally { $input.Dispose() }
    $asset.parts = $parts
    # The unsplit archive is not an upload asset. Delete it only after every part is complete.
    Remove-Item -LiteralPath $archive.FullName -Force
    return [pscustomobject]$asset
}
