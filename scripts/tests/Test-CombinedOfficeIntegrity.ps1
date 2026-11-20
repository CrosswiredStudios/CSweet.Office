#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../windows/CSweet.OfficeImageParts.ps1')
. (Join-Path $PSScriptRoot '../windows/CSweet.OfficeRelease.ps1')
$scratch = [IO.Directory]::CreateTempSubdirectory('csweet-integrity-').FullName
function Expect-Failure([scriptblock] $Operation) {
    try { & $Operation | Out-Null }
    catch { return }
    throw 'An invalid Office input was accepted.'
}
try {
    $source = Join-Path $scratch 'guest.vhdx'
    $original = [byte[]](0..250)
    [IO.File]::WriteAllBytes($source, $original)
    $expected = (Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()
    if ((Split-CSweetOfficeGuestImage $source 32) -ne 8) { throw 'Incorrect number of guest image parts.' }
    [IO.File]::Delete($source)
    $part = "$source.part0004"
    $bytes = [IO.File]::ReadAllBytes($part)
    [IO.File]::Delete($part)
    Expect-Failure { Expand-CSweetOfficeGuestImage $source $expected }
    [IO.File]::WriteAllBytes($part, [byte[]]@(4,2))
    Expect-Failure { Expand-CSweetOfficeGuestImage $source $expected }
    if ([IO.File]::Exists($source)) { throw 'Failed image restoration exposed a partial guest.' }
    [IO.File]::WriteAllBytes($part, $bytes)
    Expand-CSweetOfficeGuestImage $source $expected
    if ((Get-FileHash -LiteralPath $source).Hash -ine $expected) { throw 'Restored image differs.' }

    $media = @{schemaVersion=1; platform='windows-x64'; officeVersion='0.16.0'; assets=@(
        @{name='csweet-office-0.16.0-windows-x64.msi';size=100;sha256=$expected},
        @{name='csweet-office-0.16.0-1.cab';size=200;sha256=$expected})}
    Assert-CSweetMsiUpdateManifest $media '0.16.0'
    Expect-Failure { Assert-CSweetMsiUpdateManifest $media '0.17.0' }
    $media.assets[1].name = '../csweet-office-0.16.0-1.cab'
    Expect-Failure { Assert-CSweetMsiUpdateManifest $media '0.16.0' }
    $certificationRoot = [IO.Directory]::CreateDirectory((Join-Path $scratch 'certification')).FullName
    $evidenceFile = Join-Path $certificationRoot 'windows-hyperv.json'
    $evidence = @{providerId='hyperv-gen2'; hostOperatingSystem='windows'; hostArchitecture='x64';
        guestImageDigest="sha256:$expected"; brokerProtocolVersion='1.1'; certificationSuiteVersion='example-suite';
        certifiedAt=[DateTimeOffset]::UtcNow.AddDays(-2).ToString('O'); certificationExpiresAt=$null}
    $payload = @{officeVersion='0.16.0'; certificationEvidence='certification/windows-hyperv.json';
        guestImageDigest=$evidence.guestImageDigest; brokerProtocolVersion='1.1'; certificationSuiteVersion='example-suite';
        certifiedAt=$evidence.certifiedAt; certificationExpiresAt=$null}
    function Write-Evidence {
        [IO.File]::WriteAllText($evidenceFile, ($evidence | ConvertTo-Json))
        $payload.certificationEvidenceDigest = 'sha256:' + (Get-FileHash -LiteralPath $evidenceFile).Hash.ToLowerInvariant()
    }
    Write-Evidence
    Assert-CSweetWindowsUpdateCertification $payload $scratch '0.16.0'
    Expect-Failure { Assert-CSweetWindowsUpdateCertification $payload $scratch '0.16.1' }
    $evidence.certificationExpiresAt = [DateTimeOffset]::UtcNow.AddDays(-1).ToString('O')
    $payload.certificationExpiresAt = $evidence.certificationExpiresAt
    Write-Evidence
    Expect-Failure { Assert-CSweetWindowsUpdateCertification $payload $scratch '0.16.0' }
    $evidence.certificationExpiresAt = $payload.certificationExpiresAt = $null
    $evidence.hostOperatingSystem = 'linux'
    Write-Evidence
    Expect-Failure { Assert-CSweetWindowsUpdateCertification $payload $scratch '0.16.0' }
    $evidence.hostOperatingSystem = 'windows'
    Write-Evidence
    [IO.File]::AppendAllText($evidenceFile, ' ')
    Expect-Failure { Assert-CSweetWindowsUpdateCertification $payload $scratch '0.16.0' }
    $media.assets[1].name = 'csweet-office-0.16.0-1.cab'
    $media.assets[1].size = 3GB
    Expect-Failure { Assert-CSweetMsiUpdateManifest $media '0.16.0' }

    $key = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Office unit test', $key, 'SHA256', [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
        try {
            $approved = Join-Path $scratch 'approved.cer'
            [IO.File]::WriteAllBytes($approved, $cert.RawData)
            $json = [Text.Encoding]::UTF8.GetBytes('{"officeVersion":"0.16.0"}')
            $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($json), $false)
            $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($cert)
            $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
            $cms.ComputeSignature($signer)
            $binary = $cms.Encode()
            $encoded = [Convert]::ToBase64String($binary)
            if ((Read-CSweetSignedReleaseManifest $encoded $approved).officeVersion -cne '0.16.0') { throw 'Signed manifest failed.' }
            $binary[-1] = $binary[-1] -bxor 1
            Expect-Failure { Read-CSweetSignedReleaseManifest ([Convert]::ToBase64String($binary)) $approved }
            [IO.File]::WriteAllBytes($approved, [byte[]]@(0,1,2))
            Expect-Failure { Read-CSweetSignedReleaseManifest $encoded $approved }
            Expect-Failure { Read-CSweetSignedReleaseManifest ([Convert]::ToBase64String($json)) $approved }
        } finally { $cert.Dispose() }
    } finally { $key.Dispose() }
    Write-Output 'Guest image reconstruction, missing and corrupt parts, signed manifest, tampering, wrong publisher and unsigned metadata passed.'
} finally {
    if ([IO.Path]::GetDirectoryName($scratch) -eq [IO.Path]::GetTempPath().TrimEnd('\','/') -and
        [IO.Path]::GetFileName($scratch).StartsWith('csweet-integrity-')) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
