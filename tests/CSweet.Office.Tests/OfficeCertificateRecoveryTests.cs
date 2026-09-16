using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.Office.Contracts.ControlPlane;
using CSweet.Office.Node;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Office.Tests;

public sealed class OfficeCertificateRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "office-recovery-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpiredOrSupersededCertificateRecoversWithKeyProofAndPersistsReplacement(bool expired)
    {
        var options = new OfficeOptions { StateDirectory = _root, ControlPlaneUrl = "https://headquarters.test/" };
        var store = new OfficeStateStore(options);
        using var bootstrap = store.GetOrCreateCertificate();
        using var caKey = ECDsa.Create();
        var caRequest = new CertificateRequest("CN=Test CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var privateKey = bootstrap.GetECDsaPrivateKey()!;
        var request = new CertificateRequest("CN=Office", privateKey, HashAlgorithmName.SHA256);
        using var currentPublic = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-2),
            expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddMinutes(5), RandomNumberGenerator.GetBytes(16));
        using var current = currentPublic.CopyWithPrivateKey(privateKey);
        File.WriteAllBytes(store.GetCertificatePath(), current.Export(X509ContentType.Pfx));
        using var replacement = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        var officeId = Guid.NewGuid();
        var state = new OfficeState(officeId, "spent-receipt-must-not-be-sent", 42, store.GetCertificatePath(), "key-id", "key");
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var calls = new List<string>();
        using var handler = new Handler(async message =>
        {
            var path = message.RequestUri!.AbsolutePath;
            calls.Add(path);
            if (path.EndsWith("/challenge"))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new OfficeCertificateChallengeResponse(challenge, DateTimeOffset.UtcNow.AddMinutes(2))) };
            Assert.EndsWith("/recover", path);
            var proof = (await message.Content!.ReadFromJsonAsync<OfficeCertificateRecoveryRequest>())!;
            Assert.Equal(challenge, proof.Challenge);
            Assert.True(privateKey.VerifyData(OfficeCertificateRecoveryProof.Payload(officeId, challenge),
                Convert.FromBase64String(proof.SignatureBase64), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new OfficeCertificateResponse(true, null, "ok",
                Convert.ToBase64String(replacement.RawData), replacement.Thumbprint, replacement.NotAfter)) };
        });
        // The expired case never attempts client-certificate TLS. The superseded case is
        // exercised through recovery directly.
        var worker = new OfficeWorker(options, store, null!, null!, [], new Factory(handler),
            new ControlPlaneServerCertificateValidator(options, TimeProvider.System), NullLogger<OfficeWorker>.Instance);
        using var recovered = expired
            ? await worker.RefreshOperationalCertificateAsync(state, current, CancellationToken.None)
            : await worker.RecoverOperationalCertificateAsync(state, current, CancellationToken.None);
        Assert.Equal(replacement.Thumbprint, recovered.Thumbprint);
        using var persisted = store.GetOrCreateCertificate();
        Assert.Equal(replacement.Thumbprint, persisted.Thumbprint);
        Assert.True(persisted.HasPrivateKey);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void RejectedCertificateDoesNotOverwriteDurableIdentity()
    {
        var store = new OfficeStateStore(new OfficeOptions { StateDirectory = _root });
        using var original = store.GetOrCreateCertificate();
        var before = File.ReadAllBytes(store.GetCertificatePath());
        using var wrongKey = ECDsa.Create();
        using var wrong = new CertificateRequest("CN=Wrong", wrongKey, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Throws<ArgumentException>(() => store.InstallOperationalCertificate(original,
            Convert.ToBase64String(wrong.RawData), wrong.Thumbprint));
        Assert.Equal(before, File.ReadAllBytes(store.GetCertificatePath()));
        Assert.Throws<CryptographicException>(() => store.InstallOperationalCertificate(original,
            Convert.ToBase64String(original.RawData), "WRONG"));
        Assert.Equal(before, File.ReadAllBytes(store.GetCertificatePath()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://headquarters.test/") };
    }
}
