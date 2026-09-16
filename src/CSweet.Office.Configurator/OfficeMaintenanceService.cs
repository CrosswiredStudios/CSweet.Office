using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Office.Contracts.ControlPlane;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static class OfficeMaintenanceService
{
    public static async Task RunAsync(string[] args)
    {
        var settingsPath = args.Single(x => x.StartsWith("--settings=", StringComparison.Ordinal))["--settings=".Length..];
        var settings = JsonSerializer.Deserialize<MaintenanceSettings>(await File.ReadAllTextAsync(settingsPath))
            ?? throw new InvalidDataException("Office maintenance settings are unavailable.");
        if (!Uri.TryCreate(settings.ControlPlaneUrl, UriKind.Absolute, out var origin) || origin.Scheme != "https")
            throw new InvalidDataException("Office maintenance requires HTTPS.");
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(options => options.ServiceName = "CSweet.Office.Maintenance");
        builder.Services.AddSingleton(settings);
        builder.Services.AddHostedService<MaintenanceWorker>();
        await builder.Build().RunAsync();
    }
}

internal sealed record MaintenanceSettings(string ControlPlaneUrl, string? CertificateSha256,
    string StateDirectory, string ConfiguratorPath);

internal sealed class MaintenanceWorker(MaintenanceSettings settings, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or CryptographicException or
                JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or TaskCanceledException or ArgumentException)
            { logger.LogWarning("Office maintenance check could not complete ({ErrorType}). It will retry.", exception.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private HttpClient CreateClient(X509Certificate2? certificate = null)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (certificate is not null) handler.ClientCertificates.Add(certificate);
        if (!string.IsNullOrWhiteSpace(settings.CertificateSha256))
        {
            var expected = Convert.FromHexString(settings.CertificateSha256);
            if (expected.Length != 32) throw new InvalidDataException("Invalid Office trust pin.");
            handler.ServerCertificateCustomValidationCallback = (_, server, _, _) => server is not null &&
                CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(server.RawData));
        }
        return new HttpClient(handler) { BaseAddress = new Uri(settings.ControlPlaneUrl), Timeout = TimeSpan.FromSeconds(20) };
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        // This component has its own process and server-authenticated channel. It does not
        // require the workload service to be running or its operational certificate to be valid.
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(settings.StateDirectory, "node-state.json"), cancellationToken));
        var officeId = state.RootElement.GetProperty("OfficeId").GetGuid();
        using var identity = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(settings.StateDirectory, "node-identity.pfx"), null,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        using var recovery = CreateClient();
        using var challengeResponse = await recovery.PostAsync($"api/offices/{officeId:D}/certificate/challenge", null, cancellationToken);
        challengeResponse.EnsureSuccessStatusCode();
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<OfficeCertificateChallengeResponse>(cancellationToken)
            ?? throw new InvalidDataException("Missing maintenance identity challenge.");
        if (challenge.Challenge is null || challenge.Challenge.Length != 44 || challenge.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("Expired maintenance identity challenge.");
        using var key = identity.GetECDsaPrivateKey() ?? throw new CryptographicException("Office identity is unavailable.");
        var proof = key.SignData(OfficeCertificateRecoveryProof.Payload(officeId, challenge.Challenge), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        using var recovered = await recovery.PostAsJsonAsync($"api/offices/{officeId:D}/certificate/recover",
            new OfficeCertificateRecoveryRequest(challenge.Challenge, Convert.ToBase64String(proof)), cancellationToken);
        recovered.EnsureSuccessStatusCode();
        var issued = await recovered.Content.ReadFromJsonAsync<OfficeCertificateResponse>(cancellationToken)
            ?? throw new InvalidDataException("Missing recovered certificate.");
        if (!issued.Succeeded || issued.CertificateBase64 is null) throw new CryptographicException("Office recovery was rejected.");
        using var publicCertificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(issued.CertificateBase64));
        if (publicCertificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow ||
            publicCertificate.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
            !string.Equals(publicCertificate.Thumbprint, issued.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Invalid recovered certificate.");
        using var operational = publicCertificate.CopyWithPrivateKey(key);
        using var client = CreateClient(operational);
        using var claim = await client.PostAsync($"api/offices/{officeId:D}/maintenance/claim", null, cancellationToken);
        if (claim.StatusCode == HttpStatusCode.NoContent) return;
        claim.EnsureSuccessStatusCode();
        var handoff = await claim.Content.ReadFromJsonAsync<MaintenanceHandoff>(cancellationToken);
        if (handoff is null || !ValidHandoff(handoff.LaunchUri, officeId, new Uri(settings.ControlPlaneUrl)))
            throw new InvalidDataException("The maintenance handoff targeted another Office or server.");
        // The executable path is written by the elevated installer in its protected configuration.
        // Headquarters can request only the predefined repair/upgrade flow, never a command line.
        var start = new ProcessStartInfo(settings.ConfiguratorPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--uri");
        start.ArgumentList.Add(handoff.LaunchUri);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Recovery app could not start.");
        await process.WaitForExitAsync(cancellationToken);
        logger.LogInformation("Office maintenance request finished with exit code {ExitCode}.", process.ExitCode);
    }

    internal static bool ValidHandoff(string value, Guid officeId, Uri origin)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "csweet-office" || uri.Host != "enroll" || uri.AbsolutePath != "/v1") return false;
        try
        {
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
            return query.GetValueOrDefault("office") == officeId.ToString("D") &&
                query.GetValueOrDefault("operation") is "repair" or "upgrade" &&
                Uri.TryCreate(query.GetValueOrDefault("origin"), UriKind.Absolute, out var requested) &&
                requested.Scheme == "https" && requested.GetLeftPart(UriPartial.Authority) == origin.GetLeftPart(UriPartial.Authority) &&
                requested.AbsolutePath.TrimEnd('/') == origin.AbsolutePath.TrimEnd('/') &&
                Guid.TryParse(query.GetValueOrDefault("session"), out _) && uri.Fragment.StartsWith("#handoff=", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or IndexOutOfRangeException or UriFormatException) { return false; }
    }
    private sealed record MaintenanceHandoff(string LaunchUri);
}
