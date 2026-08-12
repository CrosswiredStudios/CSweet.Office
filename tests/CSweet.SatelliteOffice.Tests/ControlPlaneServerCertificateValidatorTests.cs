using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.SatelliteOffice.Node;
using Xunit;

namespace CSweet.SatelliteOffice.Tests;

public sealed class ControlPlaneServerCertificateValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"csweet-control-plane-trust-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PubliclyTrustedCertificateDoesNotRequirePin()
    {
        using var certificate = Certificate(Now.AddDays(-1), Now.AddDays(1));
        var validator = Validator();

        Assert.True(validator.Validate(new HttpRequestMessage(), certificate, null, SslPolicyErrors.None));
    }

    [Fact]
    public void MatchingPinAcceptsPrivateCertificateChain()
    {
        using var certificate = Certificate(Now.AddDays(-1), Now.AddDays(1));
        var validator = Validator(certificate);

        Assert.True(validator.Validate(
            new HttpRequestMessage(), certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void PinDoesNotOverrideHostnameMismatch()
    {
        using var certificate = Certificate(Now.AddDays(-1), Now.AddDays(1));
        var validator = Validator(certificate);

        Assert.False(validator.Validate(
            new HttpRequestMessage(), certificate, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void MismatchedOrExpiredPinnedCertificateIsRejected()
    {
        using var configured = Certificate(Now.AddDays(-1), Now.AddDays(1));
        using var different = Certificate(Now.AddDays(-1), Now.AddDays(1));
        using var expired = Certificate(Now.AddDays(-3), Now.AddDays(-2));
        var validator = Validator(configured);
        var expiredValidator = Validator(expired);

        Assert.False(validator.Validate(
            new HttpRequestMessage(), different, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validator.Validate(
            new HttpRequestMessage(), different, null, SslPolicyErrors.None));
        Assert.False(expiredValidator.Validate(
            new HttpRequestMessage(), expired, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    private ControlPlaneServerCertificateValidator Validator(X509Certificate2? pinned = null)
    {
        var options = new SatelliteOfficeOptions();
        if (pinned is not null)
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, $"trust-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                certificateSha256 = Convert.ToHexString(SHA256.HashData(pinned.RawData)).ToLowerInvariant()
            }));
            options.ControlPlaneTrustFilePath = path;
        }
        return new ControlPlaneServerCertificateValidator(options, new FixedTimeProvider(Now));
    }

    private static X509Certificate2 Certificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
