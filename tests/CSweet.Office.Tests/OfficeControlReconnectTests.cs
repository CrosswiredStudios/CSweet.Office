using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using CSweet.Office.Contracts.ControlPlane;
using CSweet.Office.Contracts.Security;
using CSweet.Office.Node;
using CSweet.Office.Runtime.Abstractions;
using CSweet.Office.Runtime.Core;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.Office.Tests;

public sealed class OfficeControlReconnectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlDisconnectPreservesTwoLiveWorkloadsButFencingAndIdentityRejectionStillStopThem(bool rejectIdentity)
    {
        var root = Path.Combine(Path.GetTempPath(), "office-control-reconnect", Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = new OfficeOptions { StateDirectory = root, MaximumConcurrentWorkloads = 2 };
        var store = new OfficeStateStore(options);
        var cache = new OfficeArtifactCache(options);
        var provider = new TestProvider();
        var state = new OfficeState(Guid.NewGuid(), "unused", 42, "unused", "test-key",
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        var assignments = new[] { Assignment(key, state, options, provider.Descriptor.ProviderId),
            Assignment(key, state, options, provider.Descriptor.ProviderId) };
        var invoker = new TestGateway(state, assignments);
        var worker = new OfficeWorker(options, store, new RuntimeHostInventory([]), cache, [provider],
            null!, null!, NullLogger<OfficeWorker>.Instance);
        var running = worker.RunControlConnectionsAsync(state, new OfficeGateway.OfficeGatewayClient(invoker), process.Token);
        try
        {
            await invoker.Started.Task.WaitAsync(timeout.Token);
            Assert.Equal(2, provider.Created.Count);
            Assert.Equal(2, invoker.OpenTunnels);
            invoker.First.Fail(StatusCode.Unavailable);

            var recovered = await invoker.Reconnected.Task.WaitAsync(timeout.Token);
            // Re-delivery of the same signed epochs must not create another VM.
            await recovered.Heartbeat.Task.WaitAsync(timeout.Token);
            Assert.Equal(2, provider.Created.Count);
            Assert.Empty(provider.Destroyed);
            Assert.Equal(0, invoker.ClosedTunnels);
            if (rejectIdentity)
            {
                recovered.Fail(StatusCode.PermissionDenied);
                var error = await Assert.ThrowsAsync<RpcException>(() => running);
                Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
                await provider.AllDestroyed.Task.WaitAsync(timeout.Token);
            }
            else
            {
                recovered.Responses.Writer.TryWrite(new HeadquartersControlMessage
                {
                    OfficeId = state.OfficeId.ToString("D"), SessionEpoch = state.SessionEpoch,
                    Fence = new FenceAssignment { AssignmentId = assignments[0].AssignmentId,
                        FencingEpoch = assignments[0].FencingEpoch, Reason = "test cancellation" }
                });
                await provider.FirstDestroyed.Task.WaitAsync(timeout.Token);
                Assert.Single(provider.Destroyed);
                var survivor = provider.Created.Single(x => !provider.Destroyed.Contains(x.WorkloadId));
                Assert.Equal(IsolationWorkloadState.Running, (await provider.InspectAsync(survivor))!.State);
                Assert.False(running.IsCompleted);
            }
        }
        finally
        {
            await process.CancelAsync();
            try { await running; }
            catch (OperationCanceledException) { }
            catch (RpcException) when (rejectIdentity) { }
            await provider.AllDestroyed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PendingRenewalUsesReplacementWriterAndDoesNotBlockOtherWork()
    {
        var writer = new ReconnectingControlWriter(TimeSpan.FromSeconds(2));
        var first = new ControlCall();
        writer.Attach(first);
        Assert.True(writer.IsConnected);
        first.Fail(StatusCode.Unavailable);
        var one = writer.WriteAsync(new OfficeControlMessage { OfficeId = "one" });
        var two = writer.WriteAsync(new OfficeControlMessage { OfficeId = "two" });
        Assert.False(one.IsCompleted);
        Assert.False(two.IsCompleted);
        Assert.False(writer.IsConnected);
        var next = new ControlCall();
        writer.Attach(next);
        // Cleanup from the old stream cannot detach its replacement.
        writer.Detach(first);
        await Task.WhenAll(one, two);
        Assert.Equal(2, next.Messages.Count);
    }

    [Fact]
    public async Task ReconnectWaitIsBoundedAndCancellationAndAuthorizationAreNotRetried()
    {
        Assert.False(ReconnectingControlWriter.IsTransient(new InvalidDataException("invalid session binding")));
        var writer = new ReconnectingControlWriter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(new()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(new(), cancelled.Token));
        var rejected = new ControlCall();
        rejected.Fail(StatusCode.PermissionDenied);
        writer.Attach(rejected);
        var error = await Assert.ThrowsAsync<RpcException>(() => writer.WriteAsync(new()));
        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
    }

    private static WorkloadAssignment Assignment(ECDsa key, OfficeState state, OfficeOptions options, string providerId)
    {
        byte[] artifact = [1, 2, 3];
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(artifact));
        File.WriteAllBytes(Path.Combine(options.ResolveArtifactCacheDirectory(), digest[7..] + ".artifact"), artifact);
        var image = "sha256:" + new string('a', 64);
        var workload = new RuntimeWorkloadSpecification(Guid.NewGuid(),
            new GuestImageReference("test", "1.0", image, "linux", "x64"),
            new WorkloadResourceLimits(1, 100, 512, 512, 100, 1024, TimeSpan.FromMinutes(1)),
            new BrokerChannelLease(Guid.NewGuid(), "1.0", "a-sufficiently-long-test-boot-token", image, digest, DateTimeOffset.UtcNow.AddMinutes(1)),
            new AgentArtifactReference(digest, "signature", "1.0", "linux", "x64"),
            new RuntimeAgentIdentity(Guid.NewGuid(), Guid.NewGuid().ToString("D"), Guid.NewGuid()), ["/test/agent"]);
        var json = JsonSerializer.Serialize(workload);
        var id = Guid.NewGuid();
        var issued = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds());
        var expiry = issued.AddMinutes(1);
        var hash = AssignmentEnvelope.Digest(json);
        return new WorkloadAssignment
        {
            AssignmentId = id.ToString("D"), WorkloadId = workload.WorkloadId.ToString("D"),
            FencingEpoch = 2, ProviderId = providerId, SpecificationJson = json, SpecificationSha256 = hash,
            SignatureKeyId = state.AssignmentSigningKeyId,
            IssuedAtUnixSeconds = issued.ToUnixTimeSeconds(), LeaseExpiresAtUnixSeconds = expiry.ToUnixTimeSeconds(),
            ArtifactReadToken = new string('a', 40), AuthorizationVersion = AssignmentEnvelope.CurrentAuthorizationVersion,
            Signature = ByteString.CopyFrom(key.SignData(AssignmentEnvelope.Payload(state.OfficeId, id, workload.WorkloadId,
                2, providerId, hash, issued, expiry), HashAlgorithmName.SHA256))
        };
    }

    private sealed class TestProvider : IRuntimeHostClient
    {
        private readonly InMemoryAgentIsolationProvider inner = new(new("test-provider", "Test", "1.0", "test", "test", 0,
            new(IsolationAssurance.None, false, true, true, true, true, true, true, true, true, true, false, false)));
        public IsolationProviderDescriptor Descriptor => inner.Descriptor;
        public ConcurrentBag<IsolationWorkloadHandle> Created { get; } = [];
        public ConcurrentBag<Guid> Destroyed { get; } = [];
        public TaskCompletionSource FirstDestroyed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllDestroyed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PinHeadquartersTrustAsync(PinnedHeadquartersTrust trust, CancellationToken ct = default) => Task.CompletedTask;
        public async Task<IsolationWorkloadHandle> CreateAuthorizedAsync(WorkloadSpecification spec, SignedWorkloadAuthorization auth, CancellationToken ct = default)
        { var handle = await inner.CreateAsync(spec, ct); Created.Add(handle); return handle; }
        public Task<IsolationWorkloadHandle> CreateAsync(WorkloadSpecification spec, CancellationToken ct = default) => throw new InvalidOperationException("Authorization required");
        public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);
        public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken ct = default) => inner.StartAsync(handle, ct);
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken ct = default) => inner.InspectAsync(handle, ct);
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan grace, CancellationToken ct = default) => inner.StopAsync(handle, grace, ct);
        public async Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken ct = default)
        {
            await inner.DestroyAsync(handle, ct); Destroyed.Add(handle.WorkloadId);
            FirstDestroyed.TrySetResult();
            if (Destroyed.Count == 2) AllDestroyed.TrySetResult();
        }
        public IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(IsolationWorkloadHandle handle, int bytes, CancellationToken ct = default) => inner.StreamLogsAsync(handle, bytes, ct);
        public Task<Stream> OpenGuestChannelAsync(IsolationWorkloadHandle handle, CancellationToken ct = default) => Task.FromResult<Stream>(new WaitingStream());
    }

    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ControlCall : IClientStreamWriter<OfficeControlMessage>, IAsyncStreamReader<HeadquartersControlMessage>
    {
        public Channel<HeadquartersControlMessage> Responses { get; } = Channel.CreateUnbounded<HeadquartersControlMessage>();
        public ConcurrentBag<OfficeControlMessage> Messages { get; } = [];
        public Action<OfficeControlMessage>? OnWrite { get; set; }
        private RpcException? failure;
        public TaskCompletionSource Heartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HeadquartersControlMessage Current { get; private set; } = new();
        public WriteOptions? WriteOptions { get; set; }
        public void Fail(StatusCode status) { failure = new(new(status, "test disconnect")); Responses.Writer.TryComplete(failure); }
        public Task CompleteAsync() => Task.CompletedTask;
        public Task WriteAsync(OfficeControlMessage message, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return WriteAsync(message); }
        public Task WriteAsync(OfficeControlMessage message)
        {
            if (failure is not null) throw failure;
            Messages.Add(message); OnWrite?.Invoke(message);
            if (message.BodyCase == OfficeControlMessage.BodyOneofCase.Heartbeat) Heartbeat.TrySetResult();
            return Task.CompletedTask;
        }
        public async Task<bool> MoveNext(CancellationToken ct)
        {
            try { Current = await Responses.Reader.ReadAsync(ct); return true; }
            catch (ChannelClosedException) { if (failure is not null) throw failure; return false; }
        }
    }

    private sealed class TestGateway(OfficeState state, WorkloadAssignment[] assignments) : CallInvoker
    {
        public ControlCall First { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ControlCall> Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int connections, starts, opened, closed;
        public int OpenTunnels => opened;
        public int ClosedTunnels => closed;
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
        {
            if (method.Name == "Connect")
            {
                var connection = Interlocked.Increment(ref connections) == 1 ? First : new ControlCall();
                var delivered = false;
                connection.OnWrite = message =>
                {
                    if (message.BodyCase == OfficeControlMessage.BodyOneofCase.Heartbeat && !delivered)
                    {
                        delivered = true;
                        foreach (var assignment in assignments) connection.Responses.Writer.TryWrite(new()
                        { OfficeId = state.OfficeId.ToString("D"), SessionEpoch = state.SessionEpoch, Assignment = assignment });
                        if (connection != First) Reconnected.TrySetResult(connection);
                    }
                    if (message.AssignmentStatus?.Status == "Running" && Interlocked.Increment(ref starts) == 2) Started.TrySetResult();
                };
                return (AsyncDuplexStreamingCall<TRequest, TResponse>)(object)new AsyncDuplexStreamingCall<OfficeControlMessage, HeadquartersControlMessage>(
                    connection, connection, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new(), () => { });
            }
            if (method.Name == "OpenWorkloadTunnel")
            {
                Interlocked.Increment(ref opened);
                var reader = new TunnelReader();
                return (AsyncDuplexStreamingCall<TRequest, TResponse>)(object)new AsyncDuplexStreamingCall<WorkloadTunnelFrame, WorkloadTunnelFrame>(
                    new TunnelWriter(), reader, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new(), () => Interlocked.Increment(ref closed));
            }
            throw new NotSupportedException(method.Name);
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
    }

    private sealed class TunnelWriter : IClientStreamWriter<WorkloadTunnelFrame>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(WorkloadTunnelFrame message) => Task.CompletedTask;
        public Task WriteAsync(WorkloadTunnelFrame message, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task CompleteAsync() => Task.CompletedTask;
    }
    private sealed class TunnelReader : IAsyncStreamReader<WorkloadTunnelFrame>
    {
        public WorkloadTunnelFrame Current => new();
        public async Task<bool> MoveNext(CancellationToken ct) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return false; }
    }
}
