[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][DateTimeOffset]$ExpectedExpiry
)

$ErrorActionPreference = 'Stop'
$evidence = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($EvidencePath))
try {
    $suite = $evidence.RootElement.GetProperty('certificationSuiteVersion').GetString()
    $issued = $evidence.RootElement.GetProperty('certifiedAt')
    $expires = $evidence.RootElement.GetProperty('certificationExpiresAt')
    if ([string]::IsNullOrWhiteSpace($suite) -or $issued.GetDateTimeOffset() -ge $expires.GetDateTimeOffset()) {
        throw 'Office certification evidence must name its suite and a valid certification interval.'
    }
    if ($expires.GetDateTimeOffset() -ne $ExpectedExpiry) {
        throw 'Office certification evidence must match the protected release expiry.'
    }
    # The provider compares the installed manifest against the original evidence, including fractional seconds.
    [pscustomobject]@{ Suite = $suite; CertifiedAt = $issued.GetString(); ExpiresAt = $expires.GetString() }
} finally {
    $evidence.Dispose()
}
