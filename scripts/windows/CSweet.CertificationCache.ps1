Set-StrictMode -Version Latest

# Host certification reuse for guided setup.
#
# Certification boots disposable no-network VMs and takes several minutes. A reassign, repair or retry
# shortly after a successful run on the SAME host certifies byte-identical inputs again. This cache lets
# such a run reuse that host's own recent evidence, and only when every input matches exactly:
# the guest image, the Hyper-V helper, the guest probe and the certification runner (all by SHA-256),
# and the certification profile flags. Records live in an administrator-only directory, expire after a
# bounded age, and the evidence file is re-hashed before reuse. Any mismatch runs certification again.
# RuntimeHost still verifies the installed image, signature and evidence on every probe (SEC-INV-10).

function Get-CSweetSha256Hex([Parameter(Mandatory = $true)][string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-CSweetCertificationCacheKey {
    param(
        [Parameter(Mandatory = $true)][string] $GuestImageSha256,
        [Parameter(Mandatory = $true)][string[]] $ComponentPaths,
        [Parameter(Mandatory = $true)][string] $CertificationProfile
    )
    if ($GuestImageSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'The guest image digest is invalid.' }
    $lines = @("schema=1", "image=$GuestImageSha256", "profile=$CertificationProfile")
    foreach ($component in $ComponentPaths) {
        $lines += "component=$([IO.Path]::GetFileName($component))=$(Get-CSweetSha256Hex $component)"
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha256.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant() }
    finally { $sha256.Dispose() }
}

function Initialize-CSweetCertificationCacheRoot([Parameter(Mandatory = $true)][string] $CacheRoot) {
    New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
    # Administrators and SYSTEM only: an unprivileged process must not be able to plant evidence.
    & "$env:SystemRoot\System32\icacls.exe" $CacheRoot '/inheritance:r' '/grant:r' `
        '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The certification cache directory could not be protected.' }
}

function Get-CSweetReusableCertification {
    param(
        [Parameter(Mandatory = $true)][string] $CacheRoot,
        [Parameter(Mandatory = $true)][string] $Key,
        [Parameter(Mandatory = $true)][DateTimeOffset] $Now,
        [TimeSpan] $MaximumAge = [TimeSpan]::FromHours(24)
    )
    if ($Key -cnotmatch '^[0-9a-f]{64}$') { return $null }
    $recordPath = Join-Path $CacheRoot "$Key.json"
    $evidencePath = Join-Path $CacheRoot "$Key.evidence.json"
    if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) { return $null }
    try {
        $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
        if ($record.schemaVersion -ne 1 -or [string]$record.key -cne $Key) { return $null }
        $savedAt = [DateTimeOffset]::Parse([string]$record.savedAt, [Globalization.CultureInfo]::InvariantCulture)
        if ($savedAt -gt $Now.AddMinutes(5) -or $Now - $savedAt -gt $MaximumAge) { return $null }
        if ((Get-CSweetSha256Hex $evidencePath) -cne [string]$record.evidenceSha256) { return $null }
        $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
        if ([String]::IsNullOrWhiteSpace([string]$evidence.certificationSuiteVersion) -or
            $null -eq $evidence.PSObject.Properties['checks'] -or $null -eq $evidence.checks) { return $null }
        return [IO.Path]::GetFullPath($evidencePath)
    } catch {
        return $null
    }
}

function Save-CSweetReusableCertification {
    param(
        [Parameter(Mandatory = $true)][string] $CacheRoot,
        [Parameter(Mandatory = $true)][string] $Key,
        [Parameter(Mandatory = $true)][string] $EvidencePath,
        [Parameter(Mandatory = $true)][DateTimeOffset] $Now,
        [TimeSpan] $MaximumAge = [TimeSpan]::FromHours(24)
    )
    if ($Key -cnotmatch '^[0-9a-f]{64}$') { throw 'The certification cache key is invalid.' }
    $evidenceTarget = Join-Path $CacheRoot "$Key.evidence.json"
    Copy-Item -LiteralPath $EvidencePath -Destination $evidenceTarget -Force
    $record = [ordered]@{
        schemaVersion = 1
        key = $Key
        evidenceSha256 = Get-CSweetSha256Hex $evidenceTarget
        savedAt = $Now.ToUniversalTime().ToString('O')
    }
    [IO.File]::WriteAllText((Join-Path $CacheRoot "$Key.json"), ($record | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    # Bound the cache: expired records are never reusable, so remove them.
    foreach ($stale in @(Get-ChildItem -LiteralPath $CacheRoot -File -Filter '*.json' -ErrorAction SilentlyContinue)) {
        if ($Now.UtcDateTime - $stale.LastWriteTimeUtc -gt $MaximumAge) {
            Remove-Item -LiteralPath $stale.FullName -Force -ErrorAction SilentlyContinue
        }
    }
}
