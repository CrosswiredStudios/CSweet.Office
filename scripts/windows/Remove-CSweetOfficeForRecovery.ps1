[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [int] $ParentProcessId,
    [Parameter(Mandatory = $true)] [string] $UninstallScript,
    [Parameter(Mandatory = $true)] [string] $ControlPlaneOrigin,
    [Parameter(Mandatory = $true)] [string] $HandoffSecretPath,
    [Parameter(Mandatory = $true)] [guid] $AssistedSetupSessionId,
    [Parameter(Mandatory = $true)] [string] $SetupReceiptPath,
    [string] $ControlPlaneCertificateSha256 = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$controlPlaneOriginUri = $null
if (-not [Uri]::TryCreate($ControlPlaneOrigin, [UriKind]::Absolute, [ref]$controlPlaneOriginUri) -or
    -not ($controlPlaneOriginUri.Scheme -ceq 'https' -or
        ($controlPlaneOriginUri.Scheme -ceq 'http' -and $controlPlaneOriginUri.IsLoopback))) {
    throw 'The Office removal control-plane origin is invalid.'
}
$expectedControlPlaneCertificateSha256 = $ControlPlaneCertificateSha256.Replace(':', '').Replace('-', '').ToLowerInvariant()
if (-not [String]::IsNullOrWhiteSpace($expectedControlPlaneCertificateSha256) -and
    $expectedControlPlaneCertificateSha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'The Office removal certificate fingerprint is invalid.'
}

function Invoke-CSweetPinnedRemovalRequest {
    param(
        [Parameter(Mandatory = $true)][uri] $Uri,
        [Parameter(Mandatory = $true)][string] $Body
    )

    if ($Uri.GetLeftPart([UriPartial]::Authority) -ine
        $controlPlaneOriginUri.GetLeftPart([UriPartial]::Authority)) {
        throw 'A pinned Office removal request targeted an unexpected origin.'
    }

    $previousCallback = [Net.ServicePointManager]::ServerCertificateValidationCallback
    try {
        if (-not [String]::IsNullOrWhiteSpace($expectedControlPlaneCertificateSha256)) {
            $expectedCertificateSha256 = $expectedControlPlaneCertificateSha256
            [Net.ServicePointManager]::ServerCertificateValidationCallback = {
                param($sender, $certificate, $chain, $errors)
                if ($null -eq $certificate) { return $false }
                $hasher = [Security.Cryptography.SHA256]::Create()
                try {
                    $actual = [BitConverter]::ToString(
                        $hasher.ComputeHash($certificate.GetRawCertData())).Replace('-', '').ToLowerInvariant()
                    return $actual -ceq $expectedCertificateSha256
                }
                finally { $hasher.Dispose() }
            }.GetNewClosure()
        }
        $response = Invoke-RestMethod -Method Post -Uri $Uri -ContentType 'application/json' -Body $Body
        return $response
    }
    finally {
        [Net.ServicePointManager]::ServerCertificateValidationCallback = $previousCallback
    }
}

$handoff = [IO.File]::ReadAllText($HandoffSecretPath).Trim()
$setupReceipt = [IO.File]::ReadAllText($SetupReceiptPath).Trim()
if ([String]::IsNullOrWhiteSpace($handoff) -or [String]::IsNullOrWhiteSpace($setupReceipt)) {
    throw 'The protected removal authorization is missing.'
}

try {
    Wait-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
    $productRegistration = Get-ItemProperty -LiteralPath 'HKLM:\Software\CSweet\Office' `
        -Name ProductCode -ErrorAction SilentlyContinue
    $productCode = if ($null -eq $productRegistration) { '' } else { [string]$productRegistration.ProductCode }
    if (-not [String]::IsNullOrWhiteSpace([string]$productCode)) {
        $msiexec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
        $process = Start-Process -FilePath $msiexec -Wait -PassThru -WindowStyle Hidden `
            -ArgumentList @('/x', [string]$productCode, '/qn', '/norestart', 'CSWEET_FORCE_REMOVE=1')
        if ($process.ExitCode -notin @(0, 1605, 1614)) {
            throw "The registered Office package could not be removed (exit $($process.ExitCode))."
        }
    }
    elseif (Test-Path -LiteralPath $UninstallScript -PathType Leaf) {
        & $UninstallScript -Force -Elevated
    }
    else { throw 'No validated Office removal path is available.' }

    $body = @{
        handoffSecret = $handoff
        machineName = [Environment]::MachineName
        operatingSystem = 'windows'
        architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    } | ConvertTo-Json -Compress
    $endpoint = ([Uri]::new($controlPlaneOriginUri, 'api/offices/local-sessions/removal-complete')).AbsoluteUri
    Invoke-CSweetPinnedRemovalRequest -Uri $endpoint -Body $body | Out-Null
}
catch {
    $failureBody = @{
        assistedSetupSessionId = $AssistedSetupSessionId
        setupReceipt = $setupReceipt
        resultCode = 'office_removal_failed'
        machineName = [Environment]::MachineName
        operatingSystem = 'windows'
        architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    } | ConvertTo-Json -Compress
    $failureEndpoint = ([Uri]::new($controlPlaneOriginUri, 'api/offices/local-sessions/result')).AbsoluteUri
    try { Invoke-CSweetPinnedRemovalRequest -Uri $failureEndpoint -Body $failureBody | Out-Null }
    catch { }
    throw
}
finally {
    Remove-Item -LiteralPath $HandoffSecretPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $SetupReceiptPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $UninstallScript -Force -ErrorAction SilentlyContinue
}
