$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../windows/CSweet.CertificationCache.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('office-certification-cache-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
try {
    $cache = Join-Path $root 'cache'
    New-Item -ItemType Directory -Path $cache | Out-Null
    $helper = Join-Path $root 'CSweet.Office.Runtime.HyperV.Helper.exe'
    $probe = Join-Path $root 'CSweet.Office.GuestProbe'
    $smoke = Join-Path $root 'CSweet.Office.WindowsSmokeTest.exe'
    foreach ($file in @($helper, $probe, $smoke)) { [IO.File]::WriteAllText($file, [IO.Path]::GetFileName($file)) }
    $evidence = Join-Path $root 'windows-hyperv.json'
    @{ certificationSuiteVersion = 'csweet-hardware-vm-smoke-v16'; checks = @{ networkIsolated = $true } } |
        ConvertTo-Json | Set-Content -LiteralPath $evidence -Encoding UTF8
    $image = 'a' * 64
    $now = [DateTimeOffset]::new(2026, 11, 5, 3, 0, 0, [TimeSpan]::Zero)
    $key = Get-CSweetCertificationCacheKey -GuestImageSha256 $image -ComponentPaths @($helper, $probe, $smoke) `
        -CertificationProfile 'development=True;ipv4only=True'

    Check ($null -eq (Get-CSweetReusableCertification -CacheRoot $cache -Key $key -Now $now)) 'An empty cache must not be reused.'
    Save-CSweetReusableCertification -CacheRoot $cache -Key $key -EvidencePath $evidence -Now $now
    Check ($null -ne (Get-CSweetReusableCertification -CacheRoot $cache -Key $key -Now $now.AddHours(2))) `
        'Recent evidence for identical inputs must be reusable.'

    # Any changed input produces a different key, so certification runs again.
    $otherImage = Get-CSweetCertificationCacheKey -GuestImageSha256 ('b' * 64) -ComponentPaths @($helper, $probe, $smoke) `
        -CertificationProfile 'development=True;ipv4only=True'
    $otherProfile = Get-CSweetCertificationCacheKey -GuestImageSha256 $image -ComponentPaths @($helper, $probe, $smoke) `
        -CertificationProfile 'development=False;ipv4only=True'
    [IO.File]::WriteAllText($smoke, 'changed runner')
    $otherRunner = Get-CSweetCertificationCacheKey -GuestImageSha256 $image -ComponentPaths @($helper, $probe, $smoke) `
        -CertificationProfile 'development=True;ipv4only=True'
    Check (@($otherImage, $otherProfile, $otherRunner) -notcontains $key) 'Changed image, profile or tool must change the key.'
    Check ($null -eq (Get-CSweetReusableCertification -CacheRoot $cache -Key $otherRunner -Now $now)) 'A changed runner must not reuse evidence.'

    # Bounded age, no future-dated records, and tampered evidence fail closed.
    Check ($null -eq (Get-CSweetReusableCertification -CacheRoot $cache -Key $key -Now $now.AddHours(25))) 'Expired evidence must not be reused.'
    Check ($null -eq (Get-CSweetReusableCertification -CacheRoot $cache -Key $key -Now $now.AddHours(-1))) 'Future-dated records must not be reused.'
    Add-Content -LiteralPath (Join-Path $cache "$key.evidence.json") -Value ' '
    Check ($null -eq (Get-CSweetReusableCertification -CacheRoot $cache -Key $key -Now $now.AddHours(1))) 'Tampered evidence must not be reused.'
    Check ($null -eq (Get-CSweetReusableCertification -CacheRoot $cache -Key '..\escape' -Now $now)) 'Malformed keys must be rejected.'
    Write-Host 'Certification cache checks passed.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
