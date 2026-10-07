using CSweet.Office.Contracts.ControlPlane;
using Grpc.Core;

namespace CSweet.Office.Node;

public sealed partial class OfficeWorker
{
    private async Task ReplayStopsAsync(IClientStreamWriter<OfficeControlMessage> writer,
        SemaphoreSlim writerLock, OfficeState state, CancellationToken cancellationToken)
    {
        // Existing heartbeat/reconnect discovery, bounded to preserve heartbeat/lease progress.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        foreach (var entry in _stopJournal.Discover(state.OfficeId))
        {
            if (_activeAssignments.ContainsKey(entry.AssignmentId)) continue;
            try
            {
                _providers.TryGetValue(entry.ProviderId, out var provider);
                stateStore.MarkAssignmentActive(entry.AssignmentId);
                if (!await _stopJournal.CleanupAsync(entry, provider, budget.Token)) continue;
                if (!_stopJournal.HasUnconfirmed(state.OfficeId, entry.AssignmentId))
                    stateStore.MarkAssignmentInactive(entry.AssignmentId);
                await SendAsync(writer, writerLock, new OfficeControlMessage
                {
                    ProtocolVersion = "1.0", OfficeId = state.OfficeId.ToString("D"), SessionEpoch = state.SessionEpoch,
                    AssignmentStopped = new AssignmentStopped
                    {
                        AssignmentId = entry.AssignmentId.ToString("D"), FencingEpoch = entry.FencingEpoch,
                        ProviderId = entry.ProviderId, ProviderInstanceId = entry.Handle?.ProviderInstanceId ?? "",
                        NeverCreated = entry.Handle is null
                    }
                }, budget.Token);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Assignment {AssignmentId} stop report remains pending.", entry.AssignmentId);
            }
        }
    }
}
