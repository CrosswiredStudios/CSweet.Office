using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CSweet.SatelliteOffice.Node;

internal static class ControlPlaneCertificateProbe
{
    private const string Argument = "--probe-control-plane-certificate";

    public static async Task<int?> TryRunAsync(string[] args)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, Argument, StringComparison.Ordinal));
        if (index < 0) return null;
        if (index + 1 >= args.Length || !Uri.TryCreate(args[index + 1], UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            Console.Error.WriteLine($"{Argument} requires an absolute HTTPS URL.");
            return 2;
        }

        try
        {
            var result = await ProbeAsync(uri);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"The control-plane certificate probe failed: {exception.GetBaseException().Message}");
            return 2;
        }
    }

    private static async Task<ProbeResult> ProbeAsync(Uri uri)
    {
        X509Certificate2? captured = null;
        var presentedChain = new List<X509Certificate2>();
        var capturedErrors = SslPolicyErrors.RemoteCertificateNotAvailable;
        using var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = [],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => null,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                {
                    captured?.Dispose();
                    captured = certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    foreach (var item in presentedChain) item.Dispose();
                    presentedChain.Clear();
                    if (chain is not null)
                    {
                        foreach (var element in chain.ChainElements)
                            presentedChain.Add(X509CertificateLoader.LoadCertificate(element.Certificate.RawData));
                    }
                    capturedErrors = errors;
                    return true;
                }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (captured is null)
            throw new AuthenticationException("The control plane did not present a TLS certificate.");
        using (captured)
        {
            var effectiveErrors = capturedErrors;
            if (OperatingSystem.IsWindows())
            {
                if (!BuildWithLocalMachineTrust(captured, presentedChain))
                    effectiveErrors |= SslPolicyErrors.RemoteCertificateChainErrors;
            }
            else
            {
                foreach (var item in presentedChain) item.Dispose();
            }
            return new ProbeResult(
                captured.Subject,
                captured.Issuer,
                Convert.ToHexString(SHA256.HashData(captured.RawData)).ToLowerInvariant(),
                captured.NotBefore.ToUniversalTime(),
                captured.NotAfter.ToUniversalTime(),
                (int)effectiveErrors,
                effectiveErrors.ToString());
        }
    }

    private static bool BuildWithLocalMachineTrust(
        X509Certificate2 certificate,
        IReadOnlyCollection<X509Certificate2> presentedChain)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            using (var roots = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
            {
                roots.Open(OpenFlags.ReadOnly);
                chain.ChainPolicy.CustomTrustStore.AddRange(roots.Certificates);
            }
            using (var intermediates = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine))
            {
                intermediates.Open(OpenFlags.ReadOnly);
                chain.ChainPolicy.ExtraStore.AddRange(intermediates.Certificates);
            }
            foreach (var item in presentedChain)
                chain.ChainPolicy.ExtraStore.Add(item);
            return chain.Build(certificate);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (var item in presentedChain) item.Dispose();
        }
    }

    private sealed record ProbeResult(
        string Subject,
        string Issuer,
        string CertificateSha256,
        DateTime NotBefore,
        DateTime NotAfter,
        int PolicyErrors,
        string PolicyErrorNames);
}
