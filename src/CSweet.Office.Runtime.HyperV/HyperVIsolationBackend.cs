using CSweet.Office.Runtime.Abstractions;
using CSweet.Office.Runtime.Core;

namespace CSweet.Office.Runtime.HyperV;

public sealed class HyperVIsolationBackendOptions : PlatformIsolationBackendOptions;

public sealed class HyperVIsolationBackend(HyperVIsolationBackendOptions options, TimeProvider timeProvider)
    : ExternalPlatformIsolationBackend(IsolationProviderCatalog.HyperV(), options, timeProvider),
      IPlatformWorkloadReaper, IPlatformAttemptRecovery
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

    public Task<IsolationWorkloadHandle> CreateAttemptAsync(WorkloadSpecification workload, Guid attemptKey,
        CancellationToken cancellationToken = default) => CreateWithAttemptAsync(workload, attemptKey, cancellationToken);

    public Task<AttemptShutdownResult> DestroyAttemptAsync(Guid attemptKey, Guid workloadId, WorkloadKind kind,
        CancellationToken cancellationToken = default) => DestroyWithAttemptAsync(attemptKey, workloadId, kind, cancellationToken);
}
