[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ManifestPath,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9A-Fa-f]{40}$')] [string] $CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$path = (Get-Item -LiteralPath $ManifestPath).FullName
$bytes = [IO.File]::ReadAllBytes($path)
if ($bytes.Length -gt 1MB) { throw 'The Office bootstrap manifest exceeds the signed metadata limit.' }
$cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
if (-not $cert.HasPrivateKey -or $cert.NotAfter -le (Get-Date) -or $cert.NotBefore -gt (Get-Date)) {
    throw 'A current release signing certificate with a private key is required.'
}
$cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes), $false)
$signer = [Security.Cryptography.Pkcs.CmsSigner]::new($cert)
$signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
$signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
$cms.ComputeSignature($signer)
$cms.CheckSignature($true)
$output = Join-Path ([IO.Path]::GetDirectoryName($path)) 'office-bootstrap.p7m.b64'
[IO.File]::WriteAllText($output, [Convert]::ToBase64String($cms.Encode()), [Text.Encoding]::ASCII)
