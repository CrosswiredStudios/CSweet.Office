using A = CSweet.Office.Runtime.Abstractions;
using P = CSweet.Office.Runtime.Protocol;
using Microsoft.Extensions.Logging;

namespace CSweet.Office.Runtime.LocalRpc;

public sealed partial class RuntimeHostRequestDispatcher
{
    private async Task<P.RuntimeHostEnvelope> ObserveCreationAsync(P.RuntimeHostEnvelope request, CancellationToken token)
    {
        var creation = request.ObserveCreationRequest;
        if (!Guid.TryParse(creation.WorkloadId, out var id) || !_backends.TryGetValue(creation.ProviderId, out var backend))
            return ErrorHandle(request, "invalid-workload", "Invalid workload identity or provider.");
        var gate = _operationLocks[id.GetHashCode() & 63];
        await gate.WaitAsync(token);
        try
        {
            var attempt = authorizationGate.ObserveCreation(creation);
            var handle = backend is A.IPlatformCreationObserver observer
                ? await observer.ObserveCreationAsync(attempt.Key, id, attempt.Kind, token) : attempt.Handle;
            if (handle is null || handle.WorkloadId != id || handle.Kind != attempt.Kind || handle.ProviderId != attempt.ProviderId ||
                authorizationGate.IsHandleDestroyed(handle))
                return ErrorHandle(request, "creation-unconfirmed", "The original creation has no confirmed live instance.");
            var status = await backend.InspectAsync(handle, token);
            if (status?.Handle != handle || status.State is not (A.IsolationWorkloadState.Created or A.IsolationWorkloadState.Starting or
                A.IsolationWorkloadState.Running or A.IsolationWorkloadState.Stopped))
                return ErrorHandle(request, "creation-unconfirmed", "The provider could not confirm the original instance.");
            authorizationGate.RegisterObservedHandle(creation, handle);
            return Response(request, new P.WorkloadHandleResponse { Success = true, Workload = RuntimeHostProtocolMapper.ToProtocol(handle) });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Creation observation failed for workload {WorkloadId}.", id);
            return ErrorHandle(request, "creation-unconfirmed", "The original workload could not be confirmed.");
        }
        finally { gate.Release(); }
    }

    private async Task<P.RuntimeHostEnvelope> CreateSerializedAsync(P.RuntimeHostEnvelope request, CancellationToken token)
    {
        if (!Guid.TryParse(request.CreateRequest.WorkloadId, out var id)) return ErrorHandle(request, "invalid-workload", "Invalid workload identity.");
        var gate = _operationLocks[id.GetHashCode() & 63];
        await gate.WaitAsync(token);
        try { return await CreateAsync(request, token); }
        finally { gate.Release(); }
    }

    private async Task<P.RuntimeHostEnvelope> ReconcileAttemptAsync(P.RuntimeHostEnvelope request, CancellationToken token)
    {
        var response = Base(request);
        response.ReconcileAttemptResponse = new();
        var query = request.ReconcileAttemptRequest;
        try
        {
            if (!Guid.TryParse(query.OfficeId, out var office) || !Guid.TryParse(query.AssignmentId, out var assignment))
                return response;
            var attempt = authorizationGate.GetShutdownAttempt(office, assignment, query.FencingEpoch, query.ProviderId);
            if (attempt is null) return response;
            var gate = _operationLocks[attempt.WorkloadId.GetHashCode() & 63];
            await gate.WaitAsync(token);
            try
            {
                attempt = authorizationGate.GetShutdownAttempt(office, assignment, query.FencingEpoch, query.ProviderId)!;
                var handle = attempt.Handle;
                if (!attempt.Stopped)
                {
                    if (!_backends.TryGetValue(query.ProviderId, out var backend)) return response;
                    if (handle is not null)
                    {
                        await backend.DestroyAsync(handle, token);
                        var status = await backend.InspectAsync(handle, token);
                        if (status is not null && (status.Handle != handle || status.State != A.IsolationWorkloadState.Destroyed)) return response;
                    }
                    else
                    {
                        if (backend is not A.IPlatformAttemptRecovery recovery) return response;
                        var result = await recovery.DestroyAttemptAsync(attempt.Key, attempt.WorkloadId, attempt.Kind, token);
                        if (!result.Confirmed) return response;
                        handle = result.Handle;
                    }
                    authorizationGate.RecordAttemptStopped(office, assignment, query.FencingEpoch, handle);
                }
                response.ReconcileAttemptResponse.Confirmed = true;
                if (handle is not null) response.ReconcileAttemptResponse.Workload = RuntimeHostProtocolMapper.ToProtocol(handle);
                return response;
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Attempt teardown remains unconfirmed for {AssignmentId} epoch {Epoch}.",
                query.AssignmentId, query.FencingEpoch);
            return response;
        }
    }
}
