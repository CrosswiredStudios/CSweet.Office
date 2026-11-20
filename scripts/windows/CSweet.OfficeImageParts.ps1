# Windows Installer stores the file size in a signed 32-bit column. Large signed
# guest images travel as MSI-owned parts and are verified as one image before use.
function Split-CSweetOfficeGuestImage {
    param([string] $ImagePath, [long] $PartBytes = 512MB)
    if ($PartBytes -lt 1 -or $PartBytes -gt 1GB) { throw 'Invalid guest image part size.' }
    $inputStream = [IO.File]::OpenRead($ImagePath)
    $buffer = [byte[]]::new(1MB)
    try {
        $index = 0
        while ($inputStream.Position -lt $inputStream.Length) {
            $part = '{0}.part{1:D4}' -f $ImagePath, $index
            $outputStream = [IO.File]::Open($part, 'CreateNew', 'Write', 'None')
            try {
                $remaining = $PartBytes
                while ($remaining -gt 0) {
                    $count = $inputStream.Read($buffer, 0, [int][Math]::Min($remaining, $buffer.Length))
                    if ($count -eq 0) { break }
                    $outputStream.Write($buffer, 0, $count)
                    $remaining -= $count
                }
            } finally { $outputStream.Dispose() }
            $index++
        }
        return $index
    } finally { $inputStream.Dispose() }
}

function Expand-CSweetOfficeGuestImage {
    param([string] $ImagePath, [string] $Sha256)
    if ($Sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid guest image digest.' }
    if ([IO.File]::Exists($ImagePath)) { return }
    if (-not [IO.File]::Exists("$ImagePath.part0000")) { throw 'The packaged guest image is missing.' }
    $temporary = "$ImagePath.$([guid]::NewGuid().ToString('N')).tmp"
    $outputStream = [IO.File]::Open($temporary, 'CreateNew', 'Write', 'None')
    try {
        try {
            for ($index = 0; $index -lt 1024; $index++) {
                $part = '{0}.part{1:D4}' -f $ImagePath, $index
                if (-not [IO.File]::Exists($part)) { break }
                $inputStream = [IO.File]::OpenRead($part)
                try { $inputStream.CopyTo($outputStream, 1MB) } finally { $inputStream.Dispose() }
            }
        } finally { $outputStream.Dispose() }
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ine $Sha256) {
            throw 'The packaged guest image parts failed the signed image digest check.'
        }
        try { [IO.File]::Move($temporary, $ImagePath) }
        catch [IO.IOException] {
            if (-not [IO.File]::Exists($ImagePath) -or
                (Get-FileHash -LiteralPath $ImagePath -Algorithm SHA256).Hash -ine $Sha256) { throw }
        }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}
