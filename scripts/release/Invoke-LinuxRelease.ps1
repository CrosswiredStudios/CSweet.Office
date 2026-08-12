[CmdletBinding()]
param([Parameter(Mandatory)][string]$Architecture, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$rid = "linux-$Architecture"
$payload = Join-Path $Output "$rid-payload"
$arguments = @($payload, $rid, $env:CSWEET_FIRECRACKER, $env:CSWEET_JAILER, $env:CSWEET_GUEST_KERNEL,
    $env:CSWEET_GUEST_INITRD, $env:CSWEET_GUEST_IMAGE, $env:CSWEET_GUEST_IMAGE_SIGNATURE,
    $env:CSWEET_GUEST_SIGNING_CERTIFICATE, $env:CSWEET_GUEST_SIGNING_THUMBPRINT,
    $env:CSWEET_CERTIFICATION_EVIDENCE, 'production-v1', [DateTimeOffset]::UtcNow.ToString('O'),
    $env:CSWEET_CERTIFICATION_VALID_UNTIL)
& bash (Join-Path $root 'scripts\linux\new-runtime-payload.sh') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Linux payload creation failed.' }
& bash (Join-Path $root 'scripts\linux\new-native-packages.sh') $payload $Output $Version --format all `
    --deb-signing-key $env:CSWEET_LINUX_SIGNING_KEY_ID --rpm-signing-key $env:CSWEET_LINUX_SIGNING_KEY_ID
if ($LASTEXITCODE -ne 0) { throw 'Linux package creation failed.' }
