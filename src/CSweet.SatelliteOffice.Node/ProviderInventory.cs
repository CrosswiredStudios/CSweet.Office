using System.Runtime.InteropServices;
using CSweet.SatelliteOffice.Runtime.Abstractions;
using CSweet.SatelliteOffice.Contracts.ControlPlane;

namespace CSweet.SatelliteOffice.Node;

public sealed class RuntimeHostInventory(IEnumerable<IAgentIsolationProvider> providers)
{
    public async Task<IReadOnlyList<RegisterSatelliteOfficeProviderRequest>> ProbeAsync(CancellationToken cancellationToken)
    {
        var inventory = new List<RegisterSatelliteOfficeProviderRequest>();
        foreach (var provider in providers.Where(IsCurrentPlatformProvider))
        {
            try
            {
                var probe = await provider.ProbeAsync(cancellationToken);
                var certification = probe.Certification;
                inventory.Add(new RegisterSatelliteOfficeProviderRequest(
                    probe.Descriptor.ProviderId,
                    probe.Descriptor.ProviderVersion,
                    certification?.BrokerProtocolVersion ?? "",
                    certification?.GuestImageDigest ?? "",
                    certification?.CertificationSuiteVersion ?? "",
                    certification?.EvidenceDigest ?? "",
                    certification?.CertifiedAt ?? DateTimeOffset.MinValue,
                    certification?.ExpiresAt,
                    SupportsBuilderWorkloads: true,
                    SupportsRuntimeWorkloads: true,
                    probe.IsAvailable && certification?.IsActiveAt(DateTimeOffset.UtcNow) == true,
                    Diagnostic(probe.UnavailableReason ??
                        (certification is null ? "Provider certification is unavailable." : null))));
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException or IsolationUnavailableException)
            {
                inventory.Add(new RegisterSatelliteOfficeProviderRequest(
                    provider.Descriptor.ProviderId, provider.Descriptor.ProviderVersion, "", "", "", "",
                    DateTimeOffset.MinValue, null, true, true, false,
                    $"RuntimeHost probe failed: {exception.GetType().Name}."));
            }
        }
        return inventory;
    }

    private static bool IsCurrentPlatformProvider(IAgentIsolationProvider provider) =>
        string.Equals(provider.Descriptor.HostOperatingSystem, Platform(), StringComparison.OrdinalIgnoreCase);

    private static string? Diagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains("#< CLIXML", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("<Objs Version=", StringComparison.OrdinalIgnoreCase))
            return "The runtime provider readiness check failed. Review the RuntimeHost event log for details.";
        var normalized = string.Join(' ', value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized[..Math.Min(512, normalized.Length)];
    }

    public static string Platform() => OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" :
        RuntimeInformation.OSDescription;
}
