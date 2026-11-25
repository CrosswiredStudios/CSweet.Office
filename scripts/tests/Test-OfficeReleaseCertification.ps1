$ErrorActionPreference = 'Stop'
$readCertification = Join-Path $PSScriptRoot '../release/Get-OfficeReleaseCertification.ps1'
$fixture = New-TemporaryFile
try {
    $issued = '2026-11-19T10:00:00.1234567-08:00'
    $expires = '2027-02-18T10:00:00.7654321-08:00'
    $expiryUtc = [DateTimeOffset]::Parse($expires).ToUniversalTime()
    $sample = @{ certificationSuiteVersion = 'csweet-hardware-vm-smoke-v16'; certifiedAt = $issued; certificationExpiresAt = $expires }
    [IO.File]::WriteAllText($fixture.FullName, ($sample | ConvertTo-Json))
    $metadata = & $readCertification -EvidencePath $fixture.FullName -ExpectedExpiry $expiryUtc
    if ($metadata.Suite -cne $sample.certificationSuiteVersion -or $metadata.CertifiedAt -cne $issued -or $metadata.ExpiresAt -cne $expires) {
        throw 'Packaging changed the original certification metadata.'
    }
    $rejected = $false
    try { & $readCertification -EvidencePath $fixture.FullName -ExpectedExpiry ($expiryUtc.AddTicks(1)) }
    catch { if ($_.Exception.Message -notmatch 'protected release expiry') { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Packaging accepted an expiry that differed from the evidence.' }
    Write-Output 'Release metadata preserved the certified suite, offset and fractional seconds and rejected a one-tick expiry mismatch.'
} finally {
    Remove-Item -LiteralPath $fixture.FullName
}
