using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CSweet.Office.Node;

internal static class ControlPlaneCertificateProbe
{
    private const string Argument = "--probe-control-plane-certificate";
    private const string TrustArgument = "--probe-headquarters-assignment-trust";
    private const string InitializeTrustArgument = "--initialize-headquarters-assignment-trust";

    public static async Task<int?> TryRunAsync(string[] args)
    {
        var initializeIndex = Array.FindIndex(args, value => string.Equals(value, InitializeTrustArgument, StringComparison.Ordinal));
        if (initializeIndex >= 0)
        {
            if (initializeIndex + 2 >= args.Length ||
                !Uri.TryCreate(args[initializeIndex + 1], UriKind.Absolute, out var initializeUri) ||
                initializeUri.Scheme != Uri.UriSchemeHttps ||
                !Path.IsPathFullyQualified(args[initializeIndex + 2]))
            {
                Console.Error.WriteLine($"{InitializeTrustArgument} requires an HTTPS URL and an absolute output path.");
                return 2;
            }
            try
            {
                var fingerprint = initializeIndex + 3 < args.Length
                    ? ParseRequiredFingerprint(args[initializeIndex + 3])
                    : null;
                var trust = await DownloadAssignmentTrustAsync(initializeUri, fingerprint);
                var outputPath = Path.GetFullPath(args[initializeIndex + 2]);
                var parent = Path.GetDirectoryName(outputPath) ??
                    throw new InvalidDataException("The assignment trust output path is invalid.");
                Directory.CreateDirectory(parent);
                if (File.Exists(outputPath) && File.GetAttributes(outputPath).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The assignment trust output may not be a symbolic link.");
                var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".new";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
                {
                    OfficeId = Guid.Empty,
                    trust.AssignmentSigningKeyId,
                    AssignmentVerificationPublicKey = trust.AssignmentVerificationPublicKeyBase64
                }));
                File.Move(temporary, outputPath, true);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"The Headquarters assignment trust initialization failed: {exception.GetBaseException().Message}");
                return 2;
            }
        }

        var trustIndex = Array.FindIndex(args, value => string.Equals(value, TrustArgument, StringComparison.Ordinal));
        if (trustIndex >= 0)
        {
            if (trustIndex + 1 >= args.Length || !Uri.TryCreate(args[trustIndex + 1], UriKind.Absolute, out var trustUri) ||
                trustUri.Scheme != Uri.UriSchemeHttps)
            {
                Console.Error.WriteLine($"{TrustArgument} requires an absolute HTTPS URL.");
                return 2;
            }
            try
            {
                var expectedCertificate = trustIndex + 2 < args.Length
                    ? ParseRequiredFingerprint(args[trustIndex + 2])
                    : null;
                var trust = await DownloadAssignmentTrustAsync(trustUri, expectedCertificate);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    assignmentSigningKeyId = trust.AssignmentSigningKeyId,
                    assignmentVerificationPublicKeyBase64 = trust.AssignmentVerificationPublicKeyBase64
                }));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"The Headquarters assignment trust probe failed: {exception.GetBaseException().Message}");
                return 2;
            }
        }

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

    private static async Task<AssignmentTrust> DownloadAssignmentTrustAsync(Uri uri, byte[]? expectedCertificate)
    {
        using var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (expectedCertificate is null) return errors == SslPolicyErrors.None;
                    if (certificate is null) return false;
                    var actual = SHA256.HashData(certificate.GetRawCertData());
                    return CryptographicOperations.FixedTimeEquals(actual, expectedCertificate);
                }
            }
        };
        using var client = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15) };
        var json = await client.GetStringAsync("api/offices/assignment-trust");
        using var document = JsonDocument.Parse(json);
        var keyId = document.RootElement.GetProperty("assignmentSigningKeyId").GetString();
        var keyBase64 = document.RootElement.GetProperty("assignmentVerificationPublicKeyBase64").GetString();
        var keyBytes = Convert.FromBase64String(keyBase64 ?? string.Empty);
        using var parsed = ECDsa.Create();
        parsed.ImportSubjectPublicKeyInfo(keyBytes, out var read);
        if (read != keyBytes.Length || string.IsNullOrWhiteSpace(keyId))
            throw new InvalidDataException("The Headquarters assignment trust response is invalid.");
        return new AssignmentTrust(keyId, Convert.ToBase64String(keyBytes));
    }

    private static byte[]? NormalizeFingerprint(string value)
    {
        var normalized = new string(value.Where(Uri.IsHexDigit).ToArray());
        return normalized.Length == 64 ? Convert.FromHexString(normalized) : null;
    }

    private static byte[] ParseRequiredFingerprint(string value) =>
        NormalizeFingerprint(value) ??
        throw new InvalidDataException("The expected TLS certificate fingerprint must contain exactly 64 hexadecimal characters.");

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

    private sealed record AssignmentTrust(
        string AssignmentSigningKeyId,
        string AssignmentVerificationPublicKeyBase64);
}
