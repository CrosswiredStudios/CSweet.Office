using System.Text.Json;
using A = CSweet.Office.Runtime.Abstractions;
using W = CSweet.Office.Contracts.Workloads;

namespace CSweet.Office.Runtime.LocalRpc;

public sealed partial class RuntimeHostAuthorizationGate
{
    internal sealed record ShutdownAttempt(Guid Key, Guid WorkloadId, W.WorkloadKind Kind,
        string ProviderId, A.IsolationWorkloadHandle? Handle, bool Stopped);

    private string AttemptPath(Guid officeId, Guid assignmentId, long epoch) => Path.Combine(
        _options.ResolveStateDirectory(), "assignment-attempts", officeId.ToString("N"),
        $"{assignmentId:N}-{epoch:D20}.json");

    private static ShutdownAttempt? ReadAttempt(string path) => !File.Exists(path) ? null :
        JsonSerializer.Deserialize<ShutdownAttempt>(File.ReadAllBytes(path)) ??
        throw new InvalidDataException("Invalid privileged attempt record.");

    internal ShutdownAttempt? GetShutdownAttempt(Guid officeId, Guid assignmentId, long epoch, string providerId)
    {
        lock (_sync)
        {
            if (officeId == Guid.Empty || assignmentId == Guid.Empty || epoch <= 0 ||
                string.IsNullOrWhiteSpace(providerId) || providerId.Length > 100 || ReadTrust()?.OfficeId != officeId)
                throw new InvalidDataException("Invalid teardown scope.");
            var path = AttemptPath(officeId, assignmentId, epoch);
            var attempt = ReadAttempt(path);
            if (attempt is not null)
            {
                if (attempt.ProviderId != providerId) throw new InvalidDataException("Teardown provider mismatch.");
                return attempt;
            }
            // A legacy authorized handle is still exact evidence. Never infer absence
            // from the old high-water ledger, which cannot describe older create outcomes.
            var legacy = ReadHandles().Values.SingleOrDefault(x => x.AssignmentId == assignmentId &&
                x.FencingEpoch == epoch && x.ProviderId == providerId);
            if (legacy is not null)
                attempt = new(Guid.NewGuid(), legacy.WorkloadId, (W.WorkloadKind)legacy.WorkloadKind,
                    providerId, new(providerId, legacy.WorkloadId, legacy.ProviderInstanceId,
                        (W.WorkloadKind)legacy.WorkloadKind), false);
            else if (ReadReplayState().TryGetValue(assignmentId, out var accepted) && accepted >= epoch)
                return null;
            else
                attempt = new(Guid.NewGuid(), Guid.Empty, default, providerId, null, true);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(attempt));
            return attempt;
        }
    }

    internal void RecordAttemptStopped(Guid officeId, Guid assignmentId, long epoch,
        A.IsolationWorkloadHandle? handle)
    {
        lock (_sync)
        {
            var path = AttemptPath(officeId, assignmentId, epoch);
            var attempt = ReadAttempt(path) ?? throw new InvalidDataException("Missing teardown intent.");
            if (handle is not null && (handle.ProviderId != attempt.ProviderId ||
                handle.WorkloadId != attempt.WorkloadId || handle.Kind != attempt.Kind))
                throw new InvalidDataException("Recovered workload differs from the exact attempt.");
            WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(attempt with { Handle = handle, Stopped = true }));
            if (handle is not null) RecordDestroyed(handle);
        }
    }
}
