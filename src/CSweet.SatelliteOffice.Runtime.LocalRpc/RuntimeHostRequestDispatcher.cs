using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using A = CSweet.SatelliteOffice.Runtime.Abstractions;
using W = CSweet.SatelliteOffice.Contracts.Workloads;
using P = CSweet.SatelliteOffice.Runtime.Protocol;

namespace CSweet.SatelliteOffice.Runtime.LocalRpc;

public sealed class RuntimeHostRequestDispatcher(
    IEnumerable<A.IPlatformIsolationBackend> backends,
    IEnumerable<A.IPlatformGuestChannelConnector> guestChannelConnectors,
    RuntimeHostAuthorizationGate authorizationGate,
    ILogger<RuntimeHostRequestDispatcher>? logger = null)
{
    private readonly IReadOnlyDictionary<string, A.IPlatformIsolationBackend> _backends = backends
        .ToDictionary(item => item.Descriptor.ProviderId, StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _guestChannelProviders = guestChannelConnectors
        .Select(item => item.ProviderId).ToHashSet(StringComparer.Ordinal);

    public async IAsyncEnumerable<P.RuntimeHostEnvelope> DispatchAsync(
        P.RuntimeHostEnvelope request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        switch (request.BodyCase)
        {
            case P.RuntimeHostEnvelope.BodyOneofCase.PinHeadquartersTrustRequest:
                yield return PinTrust(request);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.ProbeRequest:
                yield return await ProbeAsync(request, cancellationToken);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.CreateRequest:
                yield return await CreateAsync(request, cancellationToken);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.StartRequest:
                yield return await OperationAsync(request, request.StartRequest, static (backend, handle, token) => backend.StartAsync(handle, token), cancellationToken);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.InspectRequest:
                yield return await InspectAsync(request, cancellationToken);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.StopRequest:
                yield return await StopAsync(request, cancellationToken);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.DestroyRequest:
                yield return await OperationAsync(
                    request, request.DestroyRequest,
                    static (backend, handle, token) => backend.DestroyAsync(handle, token),
                    cancellationToken, allowTermination: true, removeAuthorization: true);
                break;
            case P.RuntimeHostEnvelope.BodyOneofCase.ReadLogsRequest:
                await foreach (var response in LogsAsync(request, cancellationToken)) yield return response;
                break;
            default:
                yield return Response(request, new P.OperationResponse
                {
                    Success = false,
                    ErrorCode = "unsupported-operation",
                    SanitizedError = "The requested runtime-host operation is not supported."
                });
                break;
        }
    }

    private P.RuntimeHostEnvelope PinTrust(P.RuntimeHostEnvelope request)
    {
        try
        {
            var pin = request.PinHeadquartersTrustRequest;
            if (!Guid.TryParse(pin.SatelliteOfficeId, out var officeId))
                throw new InvalidDataException("The Satellite Office identifier is invalid.");
            authorizationGate.Pin(new A.PinnedHeadquartersTrust(
                officeId, pin.AssignmentSigningKeyId, pin.AssignmentVerificationPublicKey.ToByteArray()));
            var response = Base(request);
            response.PinHeadquartersTrustResponse = new P.OperationResponse { Success = true };
            return response;
        }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException or IOException)
        {
            logger?.LogWarning(exception, "Rejected RuntimeHost Headquarters trust pin request {RuntimeHostRequestId}.", request.RequestId);
            var response = Base(request);
            response.PinHeadquartersTrustResponse = new P.OperationResponse
            {
                Success = false,
                ErrorCode = "headquarters-trust-rejected",
                SanitizedError = "The privileged service rejected the Headquarters assignment trust."
            };
            return response;
        }
    }

    private async Task<P.RuntimeHostEnvelope> ProbeAsync(P.RuntimeHostEnvelope request, CancellationToken cancellationToken)
    {
        if (!_backends.TryGetValue(request.ProbeRequest.ProviderId, out var backend))
            return Response(request, new P.ProbeResponse { ProviderId = request.ProbeRequest.ProviderId, UnavailableReason = "Provider is not registered." });
        A.IsolationProviderProbeResult probe;
        try
        {
            probe = await backend.ProbeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger?.LogError(
                exception,
                "RuntimeHost probe request {RuntimeHostRequestId} failed for provider {ProviderId}.",
                request.RequestId, request.ProbeRequest.ProviderId);
            return Response(request, new P.ProbeResponse
            {
                ProviderId = request.ProbeRequest.ProviderId,
                UnavailableReason = DiagnosticMessage("The provider readiness probe failed", request.RequestId)
            });
        }
        if (probe.IsAvailable && !_guestChannelProviders.Contains(probe.Descriptor.ProviderId))
            probe = probe with
            {
                IsAvailable = false,
                UnavailableReason = "The provider does not have a certified guest-channel connector installed.",
                Certification = null
            };
        return Response(request, new P.ProbeResponse
        {
            ProviderId = probe.Descriptor.ProviderId,
            ProviderVersion = probe.Descriptor.ProviderVersion,
            HostOperatingSystem = probe.Descriptor.HostOperatingSystem,
            HostArchitecture = probe.Descriptor.HostArchitecture,
            Assurance = (int)probe.Descriptor.Capabilities.Assurance,
            Available = probe.IsAvailable,
            UnavailableReason = probe.UnavailableReason ?? string.Empty,
            CertificationJson = probe.Certification is null ? string.Empty : JsonSerializer.Serialize(probe.Certification)
        });
    }

    private async Task<P.RuntimeHostEnvelope> CreateAsync(P.RuntimeHostEnvelope request, CancellationToken cancellationToken)
    {
        if (!_backends.TryGetValue(request.CreateRequest.ProviderId, out var backend)) return Error(request, "provider-not-registered");
        if (!_guestChannelProviders.Contains(request.CreateRequest.ProviderId))
            return ErrorHandle(request, "guest-channel-unavailable",
                "The provider does not have a certified guest-channel connector installed.");
        W.WorkloadSpecification workload;
        try
        {
            workload = authorizationGate.ValidateAndCommit(request.CreateRequest);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or CryptographicException)
        {
            logger?.LogWarning(
                exception,
                "RuntimeHost rejected signed authorization for request {RuntimeHostRequestId}, workload {WorkloadId}, and provider {ProviderId}.",
                request.RequestId, request.CreateRequest.WorkloadId, request.CreateRequest.ProviderId);
            return ErrorHandle(
                request,
                "authorization-rejected",
                DiagnosticMessage("Headquarters workload authorization was rejected", request.RequestId));
        }
        try
        {
            var handle = await backend.CreateAsync(workload, cancellationToken);
            EnsureProvider(handle, backend);
            try { authorizationGate.RegisterHandle(request.CreateRequest, handle); }
            catch
            {
                try { await backend.DestroyAsync(handle, CancellationToken.None); }
                catch (Exception cleanupException)
                {
                    logger?.LogError(cleanupException,
                        "RuntimeHost could not destroy workload {WorkloadId} after failing to persist its authorized handle.",
                        handle.WorkloadId);
                }
                throw;
            }
            return Response(request, new P.WorkloadHandleResponse { Success = true, Workload = RuntimeHostProtocolMapper.ToProtocol(handle) });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (A.IsolationUnavailableException exception)
        {
            logger?.LogWarning(
                exception,
                "RuntimeHost create request {RuntimeHostRequestId} could not provision workload {WorkloadId} for provider {ProviderId}.",
                request.RequestId, request.CreateRequest.WorkloadId, request.CreateRequest.ProviderId);
            return ErrorHandle(request, "provider-unavailable", exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            logger?.LogWarning(
                exception,
                "RuntimeHost create request {RuntimeHostRequestId} rejected workload {WorkloadId} for provider {ProviderId}.",
                request.RequestId, request.CreateRequest.WorkloadId, request.CreateRequest.ProviderId);
            return ErrorHandle(
                request,
                "invalid-workload",
                DiagnosticMessage("The isolated workload specification was rejected", request.RequestId));
        }
        catch (Exception exception)
        {
            logger?.LogError(
                exception,
                "RuntimeHost create request {RuntimeHostRequestId} failed for workload {WorkloadId} and provider {ProviderId}.",
                request.RequestId, request.CreateRequest.WorkloadId, request.CreateRequest.ProviderId);
            return ErrorHandle(
                request,
                "provider-create-failed",
                DiagnosticMessage("The isolation provider could not create the workload", request.RequestId));
        }
    }

    private async Task<P.RuntimeHostEnvelope> InspectAsync(P.RuntimeHostEnvelope request, CancellationToken cancellationToken)
    {
        var (backend, handle) = Resolve(request.InspectRequest);
        if (backend is null || handle is null) return Response(request, new P.WorkloadStatusResponse { Found = false });
        A.IsolationWorkloadStatus? status;
        try
        {
            status = await backend.InspectAsync(handle, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger?.LogError(
                exception,
                "RuntimeHost inspect request {RuntimeHostRequestId} failed for workload {WorkloadId} and provider {ProviderId}.",
                request.RequestId, handle.WorkloadId, handle.ProviderId);
            return Response(request, new P.WorkloadStatusResponse
            {
                Found = false,
                ErrorCode = "provider-inspect-failed",
                SanitizedError = DiagnosticMessage("The isolation provider could not inspect the workload", request.RequestId)
            });
        }
        if (status is null) return Response(request, new P.WorkloadStatusResponse { Found = false });
        EnsureProvider(status.Handle, backend);
        var response = new P.WorkloadStatusResponse
        {
            Found = true,
            Workload = RuntimeHostProtocolMapper.ToProtocol(status.Handle),
            State = (int)status.State,
            TerminationReason = (int)status.TerminationReason,
            StartedAtUnixMilliseconds = status.StartedAt?.ToUnixTimeMilliseconds() ?? 0,
            FinishedAtUnixMilliseconds = status.FinishedAt?.ToUnixTimeMilliseconds() ?? 0,
            ErrorCode = status.ErrorCode ?? string.Empty,
            SanitizedError = status.SanitizedError ?? string.Empty
        };
        if (status.ExitCode.HasValue) response.ExitCode = status.ExitCode.Value;
        return Response(request, response);
    }

    private async Task<P.RuntimeHostEnvelope> StopAsync(P.RuntimeHostEnvelope request, CancellationToken cancellationToken)
    {
        var seconds = request.StopRequest.GracePeriodSeconds;
        if (seconds is < 0 or > 300) return Error(request, "invalid-grace-period");
        return await OperationAsync(
            request,
            request.StopRequest.Workload,
            (backend, handle, token) => backend.StopAsync(handle, TimeSpan.FromSeconds(seconds), token),
            cancellationToken,
            allowTermination: true);
    }

    private async Task<P.RuntimeHostEnvelope> OperationAsync(
        P.RuntimeHostEnvelope request,
        P.WorkloadOperationRequest protocolHandle,
        Func<A.IPlatformIsolationBackend, A.IsolationWorkloadHandle, CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        bool allowTermination = false,
        bool removeAuthorization = false)
    {
        var (backend, handle) = Resolve(protocolHandle, allowTermination);
        if (backend is null || handle is null) return Error(request, "provider-not-registered");
        try
        {
            await operation(backend, handle, cancellationToken);
            if (removeAuthorization) authorizationGate.RemoveHandle(handle);
            return Response(request, new P.OperationResponse { Success = true });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (KeyNotFoundException exception)
        {
            logger?.LogWarning(
                exception,
                "RuntimeHost {Operation} request {RuntimeHostRequestId} did not find workload {WorkloadId} for provider {ProviderId}.",
                request.BodyCase, request.RequestId, handle.WorkloadId, handle.ProviderId);
            return Error(request, "workload-not-found");
        }
        catch (Exception exception)
        {
            logger?.LogError(
                exception,
                "RuntimeHost {Operation} request {RuntimeHostRequestId} failed for workload {WorkloadId} and provider {ProviderId}.",
                request.BodyCase, request.RequestId, handle.WorkloadId, handle.ProviderId);
            return Error(
                request,
                "provider-operation-failed",
                DiagnosticMessage("The isolation provider operation failed", request.RequestId));
        }
    }

    private async IAsyncEnumerable<P.RuntimeHostEnvelope> LogsAsync(P.RuntimeHostEnvelope request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var maximum = request.ReadLogsRequest.MaximumBytes;
        var (backend, handle) = Resolve(request.ReadLogsRequest.Workload);
        if (backend is null || handle is null || maximum is < 1 or > 1024 * 1024 * 1024)
        {
            yield return Response(request, new P.LogChunk { Completed = true, Truncated = true });
            yield break;
        }
        var total = 0;
        await foreach (var chunk in backend.StreamLogsAsync(handle, maximum, cancellationToken))
        {
            total = checked(total + chunk.Content.Length);
            if (total > maximum) break;
            yield return Response(request, new P.LogChunk
            {
                OccurredAtUnixMilliseconds = chunk.OccurredAt.ToUnixTimeMilliseconds(),
                Stream = chunk.Stream,
                Content = Google.Protobuf.ByteString.CopyFrom(chunk.Content.Span),
                Truncated = chunk.IsTruncated
            });
        }
        yield return Response(request, new P.LogChunk { Completed = true, Truncated = total > maximum });
    }

    private (A.IPlatformIsolationBackend? Backend, A.IsolationWorkloadHandle? Handle) Resolve(
        P.WorkloadOperationRequest protocol,
        bool allowTermination = false)
    {
        if (!_backends.TryGetValue(protocol.ProviderId, out var backend)) return (null, null);
        try
        {
            var handle = RuntimeHostProtocolMapper.FromProtocol(protocol);
            EnsureProvider(handle, backend);
            if (!authorizationGate.IsHandleAuthorized(handle, allowTermination)) return (null, null);
            return (backend, handle);
        }
        catch (InvalidDataException) { return (null, null); }
    }

    private static void EnsureProvider(A.IsolationWorkloadHandle handle, A.IAgentIsolationProvider provider)
    {
        if (!string.Equals(handle.ProviderId, provider.Descriptor.ProviderId, StringComparison.Ordinal))
            throw new InvalidDataException("The provider returned a workload handle for another provider.");
    }

    private static P.RuntimeHostEnvelope Error(
        P.RuntimeHostEnvelope request,
        string code,
        string message = "The runtime-host operation was rejected.") => Response(request, new P.OperationResponse
    {
        Success = false,
        ErrorCode = code,
        SanitizedError = message
    });

    private static P.RuntimeHostEnvelope ErrorHandle(P.RuntimeHostEnvelope request, string code, string message) => Response(request, new P.WorkloadHandleResponse
    {
        Success = false,
        ErrorCode = code,
        SanitizedError = message
    });

    private static P.RuntimeHostEnvelope Response(P.RuntimeHostEnvelope request, P.ProbeResponse body)
    {
        var response = Base(request);
        response.ProbeResponse = body;
        return response;
    }

    private static P.RuntimeHostEnvelope Response(P.RuntimeHostEnvelope request, P.WorkloadHandleResponse body)
    {
        var response = Base(request);
        response.CreateResponse = body;
        return response;
    }

    private static P.RuntimeHostEnvelope Response(P.RuntimeHostEnvelope request, P.WorkloadStatusResponse body)
    {
        var response = Base(request);
        response.InspectResponse = body;
        return response;
    }

    private static P.RuntimeHostEnvelope Response(P.RuntimeHostEnvelope request, P.OperationResponse body)
    {
        var response = Base(request);
        response.OperationResponse = body;
        return response;
    }

    private static P.RuntimeHostEnvelope Response(P.RuntimeHostEnvelope request, P.LogChunk body)
    {
        var response = Base(request);
        response.LogChunk = body;
        return response;
    }

    private static P.RuntimeHostEnvelope Base(P.RuntimeHostEnvelope request) => new()
    {
        ProtocolVersion = request.ProtocolVersion,
        RequestId = request.RequestId
    };

    private static string DiagnosticMessage(string message, string requestId) =>
        $"{message}. Diagnostic request: {requestId}.";
}
