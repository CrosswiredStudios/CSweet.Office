using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CSweet.Office.Node;

namespace CSweet.Office.Tests;

public sealed class OfficeCertificateTlsTests
{
    [Fact]
    public async Task NewTlsConnectionsUseRenewedCertificateAndStillValidateServerPin()
    {
        using var keys = new TestKeys();
        using var serverKey = keys.CreateRsa();
        var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var serverCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var clientKey = keys.CreateEc();
        var clientRequest = new CertificateRequest("CN=Office", clientKey, HashAlgorithmName.SHA256);
        clientRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        clientRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        using var original = clientRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var rotated = clientRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificates = new OfficeCertificateLease(original);
        var validator = new ControlPlaneServerCertificateValidator(new OfficeOptions
        {
            ControlPlaneCertificateSha256 = Convert.ToHexString(SHA256.HashData(serverCertificate.RawData))
        }, TimeProvider.System);
        using var handler = validator.CreateRotatingHttpHandler(() => certificates.Current);
        using var http = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observed = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
                using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowTlsResume = false
                }, timeout.Token);
                Assert.NotNull(tls.RemoteCertificate);
                observed.Add(tls.RemoteCertificate.GetCertHashString());
                using var reader = new StreamReader(tls, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
                await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), timeout.Token);
            }
        }, timeout.Token);
        try { Assert.Equal("ok", await http.GetStringAsync($"https://localhost:{port}/", timeout.Token)); }
        catch { timeout.Cancel(); await server; throw; }
        certificates.Update(rotated);
        Assert.Equal("ok", await http.GetStringAsync($"https://localhost:{port}/", timeout.Token));
        await server;
        Assert.Equal(new[] { original.Thumbprint, rotated.Thumbprint }, observed);
        // The rotating handler shares the same strict pin validator as ordinary requests.
        Assert.False(validator.Validate(new HttpRequestMessage(), original, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }
    private sealed class TestKeys : IDisposable
    {
        private readonly List<CngKey> _keys = [];
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private CngKey Create(CngAlgorithm algorithm)
        {
            var key = CngKey.Create(algorithm, $"csweet-office-tls-test-{Guid.NewGuid():N}", new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.AllowExport | CngExportPolicies.AllowPlaintextExport,
                KeyUsage = CngKeyUsages.AllUsages
            });
            _keys.Add(key);
            return key;
        }
        public RSA CreateRsa() => OperatingSystem.IsWindows() ? new RSACng(Create(CngAlgorithm.Rsa)) : RSA.Create(2048);
        public ECDsa CreateEc() => OperatingSystem.IsWindows() ? new ECDsaCng(Create(CngAlgorithm.ECDsaP256)) : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public void Dispose()
        {
            foreach (var key in _keys) { key.Delete(); key.Dispose(); }
        }
    }

}
