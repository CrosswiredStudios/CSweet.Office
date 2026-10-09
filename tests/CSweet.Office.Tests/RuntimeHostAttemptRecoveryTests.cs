using System.Security.Cryptography;
using CSweet.Office.Runtime.Abstractions;
using CSweet.Office.Runtime.Core;
using CSweet.Office.Runtime.LocalRpc;
using CSweet.Office.Runtime.Protocol;

namespace CSweet.Office.Tests;

public sealed partial class RuntimeHostRpcIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactAttemptRecoverySurvivesLostHandleOrStopResponse(bool lostHandle)
    {
        var root = Path.Combine(Path.GetTempPath(), $"attempt-recovery-{Guid.NewGuid():N}");
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var options = new RuntimeHostAuthorizationOptions { StateDirectory = root };
            var gate = new RuntimeHostAuthorizationGate(options, TimeProvider.System);
            var trust = Trust(key, Guid.NewGuid());
            gate.Pin(trust);
            var backend = new RecoveryBackend(new InMemoryAgentIsolationProvider(Descriptor()));
            var workload = Runtime();
            var auth = Authorization(key, trust, backend.Descriptor.ProviderId, workload);
            var create = RuntimeHostProtocolMapper.ToProtocol(backend.Descriptor.ProviderId, workload);
            create.Authorization = ToProtocol(auth);
            backend.ThrowAfterCreate = lostHandle;
            var creating = new RuntimeHostRequestDispatcher([backend], [new TestGuestConnector(backend.Descriptor.ProviderId)], gate);
            var created = await RunAttempt(creating, new RuntimeHostEnvelope { CreateRequest = create });
            Assert.Equal(!lostHandle, created.CreateResponse.Success);
            var handle = backend.LastHandle!;
            // Simulate process restart after creation completed but its response was lost.
            gate = new(options, TimeProvider.System);
            var dispatcher = new RuntimeHostRequestDispatcher([backend], [], gate);
            var recovery = new RuntimeHostEnvelope { ReconcileAttemptRequest = new()
            {
                OfficeId = trust.OfficeId.ToString("D"), AssignmentId = auth.AssignmentId.ToString("D"),
                FencingEpoch = auth.FencingEpoch, ProviderId = auth.ProviderId
            }};
            backend.Confirm = false;
            Assert.False((await RunAttempt(dispatcher, recovery)).ReconcileAttemptResponse.Confirmed);
            backend.Confirm = true;
            var result = (await RunAttempt(dispatcher, recovery)).ReconcileAttemptResponse;
            Assert.True(result.Confirmed);
            Assert.Equal(handle.ProviderInstanceId, result.Workload.ProviderInstanceId);
            Assert.False(gate.IsHandleAuthorized(handle, true));
            // Stop receipt replay needs no backend and cannot revive execution.
            dispatcher = new([], [], new(options, TimeProvider.System));
            Assert.True((await RunAttempt(dispatcher, recovery)).ReconcileAttemptResponse.Confirmed);
            Assert.Throws<InvalidDataException>(() => gate.ValidateAndCommit(create));
            recovery.ReconcileAttemptRequest.OfficeId = Guid.NewGuid().ToString("D");
            Assert.False((await RunAttempt(dispatcher, recovery)).ReconcileAttemptResponse.Confirmed);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NeverAcceptedAttemptIsFencedBeforeDelayedCreation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var gate = NewGate();
        var trust = Trust(key, Guid.NewGuid());
        gate.Pin(trust);
        var workload = Runtime();
        var auth = Authorization(key, trust, Descriptor().ProviderId, workload);
        var dispatcher = new RuntimeHostRequestDispatcher([], [], gate);
        var result = await RunAttempt(dispatcher, new RuntimeHostEnvelope { ReconcileAttemptRequest = new()
        {
            OfficeId = trust.OfficeId.ToString("D"), AssignmentId = auth.AssignmentId.ToString("D"),
            FencingEpoch = auth.FencingEpoch, ProviderId = auth.ProviderId
        }});
        Assert.True(result.ReconcileAttemptResponse.Confirmed);
        Assert.Null(result.ReconcileAttemptResponse.Workload);
        var create = RuntimeHostProtocolMapper.ToProtocol(auth.ProviderId, workload);
        create.Authorization = ToProtocol(auth);
        Assert.Throws<InvalidDataException>(() => gate.ValidateAndCommit(create));
    }

    private static async Task<RuntimeHostEnvelope> RunAttempt(RuntimeHostRequestDispatcher dispatcher, RuntimeHostEnvelope request)
    {
        await foreach (var response in dispatcher.DispatchAsync(request)) return response;
        throw new InvalidOperationException("Missing response.");
    }

    [Fact]
    public async Task OlderHandleRemainsRecoverableAfterNewerEpochReplacesCurrentHandle()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var gate = NewGate(); var trust = Trust(key, Guid.NewGuid()); gate.Pin(trust);
        var backend = new RecoveryBackend(new InMemoryAgentIsolationProvider(Descriptor())) { Confirm = true };
        var workload = Runtime(); var assignmentId = Guid.NewGuid();
        var dispatcher = new RuntimeHostRequestDispatcher([backend], [new TestGuestConnector(backend.Descriptor.ProviderId)], gate);
        var handles = new List<IsolationWorkloadHandle>();
        foreach (var epoch in new[] { 2L, 4L })
        {
            var now = DateTimeOffset.UtcNow;
            var json = System.Text.Json.JsonSerializer.Serialize(workload, workload.GetType());
            var digest = CSweet.Office.Contracts.Security.AssignmentEnvelope.Digest(json);
            var signature = key.SignData(CSweet.Office.Contracts.Security.AssignmentEnvelope.Payload(
                trust.OfficeId, assignmentId, workload.WorkloadId, epoch, backend.Descriptor.ProviderId,
                digest, now, now.AddMinutes(1)), HashAlgorithmName.SHA256);
            var auth = new CSweet.Office.Runtime.Abstractions.SignedWorkloadAuthorization(
                CSweet.Office.Contracts.Security.AssignmentEnvelope.CurrentAuthorizationVersion, trust.OfficeId, assignmentId, workload.WorkloadId,
                epoch, backend.Descriptor.ProviderId, json, digest, trust.AssignmentSigningKeyId, signature, now, now.AddMinutes(1));
            var request = RuntimeHostProtocolMapper.ToProtocol(auth.ProviderId, workload);
            request.Authorization = ToProtocol(auth);
            var created = (await RunAttempt(dispatcher, new() { CreateRequest = request })).CreateResponse;
            Assert.True(created.Success, created.ErrorCode + ": " + created.SanitizedError);
            handles.Add(backend.LastHandle!);
        }
        var old = await RunAttempt(dispatcher, new() { ReconcileAttemptRequest = new()
        {
            OfficeId = trust.OfficeId.ToString("D"), AssignmentId = assignmentId.ToString("D"),
            FencingEpoch = 2, ProviderId = backend.Descriptor.ProviderId
        }});
        Assert.True(old.ReconcileAttemptResponse.Confirmed);
        Assert.Equal(handles[0].ProviderInstanceId, old.ReconcileAttemptResponse.Workload.ProviderInstanceId);
        Assert.True(gate.IsHandleAuthorized(handles[1]));
        Assert.False(gate.IsHandleAuthorized(handles[0], true));
    }

    private sealed class RecoveryBackend(InMemoryAgentIsolationProvider inner) : IPlatformIsolationBackend, IPlatformAttemptRecovery
    {
        public bool Confirm { get; set; }
        public bool ThrowAfterCreate { get; set; }
        public IsolationWorkloadHandle? LastHandle { get; private set; }
        private readonly Dictionary<Guid, IsolationWorkloadHandle> _handles = [];
        private readonly HashSet<IsolationWorkloadHandle> _destroyed = [];
        public IsolationProviderDescriptor Descriptor => inner.Descriptor;
        public async Task<IsolationWorkloadHandle> CreateAttemptAsync(WorkloadSpecification workload, Guid attemptKey, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            var handle = new IsolationWorkloadHandle(Descriptor.ProviderId, workload.WorkloadId,
                $"attempt-{attemptKey:N}", workload.Kind);
            _handles.Add(attemptKey, handle); LastHandle = handle;
            if (ThrowAfterCreate) throw new IOException("Creation response lost.");
            return handle;
        }
        public async Task<AttemptShutdownResult> DestroyAttemptAsync(Guid attemptKey, Guid workloadId, WorkloadKind kind, CancellationToken cancellationToken = default)
        {
            if (!Confirm) return new(false);
            var handle = _handles[attemptKey];
            Assert.Equal(workloadId, handle.WorkloadId);
            Assert.Equal(kind, handle.Kind);
            await DestroyAsync(handle, cancellationToken);
            return new(true, handle);
        }
        public Task<IsolationWorkloadHandle> CreateAsync(WorkloadSpecification workload, CancellationToken cancellationToken = default) => inner.CreateAsync(workload, cancellationToken);
        public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => inner.ProbeAsync(cancellationToken);
        public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => inner.StartAsync(handle, cancellationToken);
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => inner.StopAsync(handle, gracePeriod, cancellationToken);
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult<IsolationWorkloadStatus?>(new(handle, _destroyed.Contains(handle) ?
                IsolationWorkloadState.Destroyed : IsolationWorkloadState.Created,
                IsolationTerminationReason.None, null, null, null, null, null));
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
        { if (Confirm) _destroyed.Add(handle); return Task.CompletedTask; }
        public IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, CancellationToken cancellationToken = default) => inner.StreamLogsAsync(handle, maximumBytes, cancellationToken);
    }
}
