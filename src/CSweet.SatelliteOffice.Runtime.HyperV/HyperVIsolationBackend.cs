using CSweet.SatelliteOffice.Runtime.Abstractions;
using CSweet.SatelliteOffice.Runtime.Core;

namespace CSweet.SatelliteOffice.Runtime.HyperV;

public sealed class HyperVIsolationBackendOptions : PlatformIsolationBackendOptions;

public sealed class HyperVIsolationBackend(HyperVIsolationBackendOptions options, TimeProvider timeProvider)
    : ExternalPlatformIsolationBackend(IsolationProviderCatalog.HyperV(), options, timeProvider),
      IPlatformWorkloadReaper
{
    protected override bool IsHostPlatform(out string unavailableReason)
    {
        unavailableReason = OperatingSystem.IsWindows()
            ? string.Empty
            : "Hyper-V requires a Windows host.";
        return OperatingSystem.IsWindows();
    }

    Task<int> IPlatformWorkloadReaper.ReapAbandonedWorkloadsAsync(CancellationToken cancellationToken) =>
        ReapAbandonedWorkloadsAsync(cancellationToken);
}
