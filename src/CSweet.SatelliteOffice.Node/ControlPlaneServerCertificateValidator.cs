using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CSweet.SatelliteOffice.Node;

public sealed class ControlPlaneServerCertificateValidator
{
    private readonly byte[]? _certificateSha256;
    private readonly TimeProvider _timeProvider;

    public ControlPlaneServerCertificateValidator(SatelliteOfficeOptions options, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        var configured = options.ControlPlaneCertificateSha256;
        if (string.IsNullOrWhiteSpace(configured) && !string.IsNullOrWhiteSpace(options.ControlPlaneTrustFilePath))
            configured = ReadPin(options.ControlPlaneTrustFilePath);
        _certificateSha256 = ParsePin(configured);
    }

    public HttpClientHandler CreateHttpClientHandler(X509Certificate2? clientCertificate = null)
    {
        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = Validate
        };
        if (clientCertificate is not null)
            handler.ClientCertificates.Add(clientCertificate);
        return handler;
    }

    internal bool Validate(
        HttpRequestMessage _,
        X509Certificate2? certificate,
        X509Chain? __,
        SslPolicyErrors policyErrors)
    {
        if (_certificateSha256 is null)
            return policyErrors == SslPolicyErrors.None;
        if (certificate is null ||
            (policyErrors & (SslPolicyErrors.RemoteCertificateNotAvailable |
                             SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            return false;

        var now = _timeProvider.GetUtcNow();
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            return false;

        var actual = SHA256.HashData(certificate.RawData);
        return CryptographicOperations.FixedTimeEquals(actual, _certificateSha256);
    }

    private static string ReadPin(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(path)));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) ||
                !schemaVersion.TryGetInt32(out var version) || version != 1 ||
                !document.RootElement.TryGetProperty("certificateSha256", out var property) ||
                property.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The control-plane trust file is invalid.");
            return property.GetString() ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("The control-plane trust file could not be read.", exception);
        }
    }

    private static byte[]? ParsePin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (normalized.StartsWith("sha256", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[6..].TrimStart(':');
        if (normalized.Length != 64)
            throw new InvalidDataException("The control-plane certificate SHA-256 fingerprint must contain 64 hexadecimal characters.");
        try { return Convert.FromHexString(normalized); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The control-plane certificate SHA-256 fingerprint is invalid.", exception);
        }
    }
}
