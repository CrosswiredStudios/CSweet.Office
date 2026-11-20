using System.Security.Cryptography;
using CSweet.Office.Runtime.Abstractions;
using CSweet.Office.Runtime.Core;
using CSweet.Office.Runtime.LocalRpc;
using CSweet.Office.Runtime.Protocol;

namespace CSweet.Office.Tests;

public sealed partial class RuntimeHostRpcIntegrationTests
{
    [Fact]
    public async Task ClientObservesAfterLostHelperResponseWithoutRepeatingCreation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust(key, Guid.NewGuid());
        var gate = NewGate();
        gate.Pin(trust);
        var descriptor = Descriptor();
        var backend = new RecoveryBackend(new InMemoryAgentIsolationProvider(descriptor)) { ThrowAfterCreate = true };
        var connector = new TestGuestConnector(descriptor.ProviderId);
        var endpoint = new RuntimeHostEndpointOptions
        {
            NamedPipeName = $"csweet-observe-{Guid.NewGuid():N}",
            UnixSocketPath = Path.Combine(Path.GetTempPath(), $"csweet-observe-{Guid.NewGuid():N}.sock"),
            ConnectTimeoutSeconds = 5
        };
        if (OperatingSystem.IsWindows())
            endpoint.AllowedClientSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        var options = new RuntimeHostAuthenticationOptions
        {
            KeyId = "observation-test", SharedKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        };
        var server = new RuntimeHostRpcServer(endpoint, new(options, TimeProvider.System),
            new([backend], [connector], gate), [connector], gate);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = server.RunAsync(stop.Token);
        try
        {
            var client = new RuntimeHostProviderClient(descriptor, endpoint, new(options, TimeProvider.System));
            var workload = Runtime();
            var handle = await client.CreateAuthorizedAsync(workload, Authorization(key, trust, descriptor.ProviderId, workload), stop.Token);
            Assert.Equal(backend.LastHandle, handle);
            Assert.Equal(1, backend.Creates);
            Assert.True(gate.IsHandleAuthorized(handle));
        }
        finally
        {
            await stop.CancelAsync();
            try { await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedCreationObservationReturnsTheOriginalHandleAfterReceiverRestart(bool lostHelperResponse)
    {
        var root = Path.Combine(Path.GetTempPath(), $"observe-create-{Guid.NewGuid():N}");
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trust = Trust(key, Guid.NewGuid());
            var options = new RuntimeHostAuthorizationOptions { StateDirectory = root };
            var gate = new RuntimeHostAuthorizationGate(options, TimeProvider.System);
            gate.Pin(trust);
            var workload = Runtime();
            var backend = new RecoveryBackend(new InMemoryAgentIsolationProvider(Descriptor())) { ThrowAfterCreate = lostHelperResponse };
            var create = RuntimeHostProtocolMapper.ToProtocol(backend.Descriptor.ProviderId, workload);
            create.Authorization = ToProtocol(Authorization(key, trust, backend.Descriptor.ProviderId, workload));
            var connector = new TestGuestConnector(backend.Descriptor.ProviderId);
            var receiver = new RuntimeHostRequestDispatcher([backend], [connector], gate);
            var sent = await RunAttempt(receiver, new() { CreateRequest = create });
            Assert.Equal(!lostHelperResponse, sent.CreateResponse.Success);
            // The caller discarded any response. Reopen the real RuntimeHost journal.
            var restarted = new RuntimeHostAuthorizationGate(options, TimeProvider.System);
            receiver = new RuntimeHostRequestDispatcher([backend], [connector], restarted);
            var observe = new RuntimeHostEnvelope { ObserveCreationRequest = create };
            var result = (await RunAttempt(receiver, observe)).CreateResponse;
            Assert.True(result.Success, result.SanitizedError);
            Assert.Equal(backend.LastHandle!.ProviderInstanceId, result.Workload.ProviderInstanceId);
            Assert.True(restarted.IsHandleAuthorized(backend.LastHandle));
            Assert.Equal(1, backend.Creates);
            Assert.Throws<InvalidDataException>(() => restarted.ValidateAndCommit(create));
            var altered = create.Clone();
            altered.Authorization.Signature = Google.Protobuf.ByteString.CopyFrom(new byte[64]);
            Assert.False((await RunAttempt(receiver, new() { ObserveCreationRequest = altered })).CreateResponse.Success);
            backend.Confirm = true;
            var destroyed = await RunAttempt(receiver, new() { DestroyRequest = RuntimeHostProtocolMapper.ToProtocol(backend.LastHandle) });
            Assert.True(destroyed.OperationResponse.Success);
            Assert.False((await RunAttempt(receiver, observe)).CreateResponse.Success);
            Assert.False(restarted.IsHandleAuthorized(backend.LastHandle));
            Assert.Equal(1, backend.Creates);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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

    private sealed class RecoveryBackend(InMemoryAgentIsolationProvider inner) : IPlatformIsolationBackend, IPlatformAttemptRecovery, IPlatformCreationObserver
    {
        public bool Confirm { get; set; }
        public bool ThrowAfterCreate { get; set; }
        public int Creates { get; private set; }
        public IsolationWorkloadHandle? LastHandle { get; private set; }
        private readonly Dictionary<Guid, IsolationWorkloadHandle> _handles = [];
        private readonly HashSet<IsolationWorkloadHandle> _destroyed = [];
        public IsolationProviderDescriptor Descriptor => inner.Descriptor;
        public async Task<IsolationWorkloadHandle> CreateAttemptAsync(WorkloadSpecification workload, Guid attemptKey, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            Creates++;
            var handle = new IsolationWorkloadHandle(Descriptor.ProviderId, workload.WorkloadId,
                $"attempt-{attemptKey:N}", workload.Kind);
            _handles.Add(attemptKey, handle); LastHandle = handle;
            if (ThrowAfterCreate) throw new IOException("Creation response lost.");
            return handle;
        }
        public Task<IsolationWorkloadHandle?> ObserveCreationAsync(Guid attemptKey, Guid workloadId, WorkloadKind kind, CancellationToken cancellationToken = default)
        {
            var handle = _handles.GetValueOrDefault(attemptKey);
            return Task.FromResult(handle?.WorkloadId == workloadId && handle.Kind == kind ? handle : null);
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
