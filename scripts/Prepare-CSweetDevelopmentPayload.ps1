[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $PayloadDirectory,
    [ValidateSet('x64', 'arm64')][string] $Architecture = 'x64'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payload = [IO.Path]::GetFullPath($PayloadDirectory)
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$lockPath = Join-Path $root 'build/development-tools.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$artifacts = $lock.architectures.$Architecture
foreach ($download in @(
    @{ Name = 'node.tar.xz'; Url = $artifacts.nodeUrl; Hash = $artifacts.nodeSha256; Algorithm = 'SHA256' },
    @{ Name = 'dotnet.tar.gz'; Url = $artifacts.dotnetUrl; Hash = $artifacts.dotnetSha512; Algorithm = 'SHA512' },
    @{ Name = 'tun2socks.zip'; Url = $artifacts.tunUrl; Hash = $artifacts.tunSha256; Algorithm = 'SHA256' }
)) {
    $target = Join-Path $payload $download.Name
    if (-not (Test-Path -LiteralPath $target)) {
        Invoke-WebRequest -Uri $download.Url -OutFile $target -UseBasicParsing
    }
    $actual = (Get-FileHash -LiteralPath $target -Algorithm $download.Algorithm).Hash.ToLowerInvariant()
    if ($actual -ne $download.Hash) { throw "Pinned development payload checksum mismatch: $($download.Name)" }
}
Copy-Item -LiteralPath $lockPath -Destination (Join-Path $payload 'development-tools.lock.json')
Copy-Item -LiteralPath (Join-Path $root 'build/provision-development.sh') -Destination (Join-Path $payload 'provision-development.sh')
Set-Content -LiteralPath (Join-Path $payload 'development-architecture') -Value $Architecture -Encoding Ascii

Copy-Item -LiteralPath (Join-Path $root 'build/provision-node-toolchain.sh') -Destination (Join-Path $payload 'provision-node-toolchain.sh')
Copy-Item -LiteralPath (Join-Path $root 'build/toolchains/node-typescript') -Destination (Join-Path $payload 'node-toolchain') -Recurse
