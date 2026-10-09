using CSweet.Office.Runtime.Core;

namespace CSweet.Office.Runtime.HyperV.Helper;

internal interface IHyperVAttemptOperations
{
    Task<Guid?> FindAsync(string vmName);
    Task DestroyAsync(string vmName);
}

internal sealed class HyperVAttemptOperations : IHyperVAttemptOperations
{
    public async Task<Guid?> FindAsync(string vmName)
    {
        var value = await PowerShellHyperV.GetIdAsync(vmName);
        return string.IsNullOrWhiteSpace(value) ? null :
            Guid.TryParse(value, out var id) && id != Guid.Empty ? id :
            throw new InvalidDataException("Hyper-V returned an invalid VM identity.");
    }
    public async Task DestroyAsync(string vmName) => await PowerShellHyperV.DestroyAsync(vmName);
}

internal sealed partial class HyperVHelperController
{
    private async Task<PlatformHelperResponse> DestroyAttemptAsync(PlatformHelperRequest request)
    {
        if (request.AttemptKey is not { } key || key == Guid.Empty ||
            request.RecoveryWorkloadId is not { } workload || workload == Guid.Empty ||
            request.RecoveryWorkloadKind is not { } kind || !Enum.IsDefined(kind))
            return Failure("invalid-attempt", "Invalid attempt recovery scope.");
        using var journal = new HyperVAttemptJournal(paths, key);
        var entry = journal.Read() ?? new HyperVAttempt(key, workload, kind);
        if (entry.Key != key || entry.WorkloadId != workload || entry.Kind != kind)
            return Failure("invalid-attempt", "The creation journal differs from the recovery scope.");
        if (!entry.Stopped)
        {
            var operations = attemptOperations ?? new HyperVAttemptOperations();
            // A missing journal means create never entered this critical section. The
            // permanent tombstone prevents a delayed create from starting afterwards.
            var vmId = await operations.FindAsync(entry.VmName);
            if (vmId is { } id)
            {
                if (id == Guid.Empty || entry.InstanceId is { } recorded && recorded != id)
                    return Failure("invalid-attempt", "Hyper-V returned a different instance identity.");
                entry = entry with { InstanceId = id };
                journal.Save(entry);
                await operations.DestroyAsync(entry.VmName);
                if (await operations.FindAsync(entry.VmName) is not null)
                    return Failure("destruction-unconfirmed", "Hyper-V teardown is not confirmed.");
            }
            if (entry.InstanceId is { } instanceId) DeleteInstanceDirectory(paths.InstanceDirectory(instanceId));
            entry = entry with { Stopped = true };
            journal.Save(entry);
        }
        return new PlatformHelperResponse { Success = true, ProviderInstanceId = entry.InstanceId?.ToString("N") };
    }
}
