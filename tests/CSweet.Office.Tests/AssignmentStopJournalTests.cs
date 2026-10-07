using CSweet.Office.Node;
using CSweet.Office.Runtime.Abstractions;

namespace CSweet.Office.Tests;

public sealed class AssignmentStopJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"office-stop-tests-{Guid.NewGuid():N}");
    private readonly Guid _office = Guid.NewGuid();
    private readonly Guid _assignment = Guid.NewGuid();
    private AssignmentStopJournal Open() => new(new OfficeOptions { StateDirectory = _root });

    [Fact]
    public async Task PreparedAttemptCanBeRetiredButCannotExecuteAfterReceiptOrRestart()
    {
        var journal = Open();
        Assert.True(journal.TryBegin(_office, _assignment, 2, "test"));
        journal = Open();
        Assert.True(await journal.CleanupAsync(journal.Read(_office, _assignment, 2), null, default));
        Assert.Equal("stopped", Assert.Single(Open().Discover(_office)).Phase);
        journal.Acknowledge(Guid.NewGuid(), _assignment, 2);
        journal.Acknowledge(_office, _assignment, 3);
        Assert.Single(Open().Discover(_office));
        journal.Acknowledge(_office, _assignment, 2);
        Assert.Empty(Open().Discover(_office));
        Assert.False(Open().TryBegin(_office, _assignment, 2, "test"));
        Assert.True(Open().TryBegin(_office, _assignment, 3, "test"));
    }

    [Fact]
    public async Task UnknownCreationOutcomeCannotBecomeNeverCreatedProof()
    {
        var journal = Open();
        journal.TryBegin(_office, _assignment, 2, "test");
        journal.BeginCreate(journal.Read(_office, _assignment, 2));
        journal = Open();
        Assert.False(await journal.CleanupAsync(journal.Read(_office, _assignment, 2), null, default));
        journal.Acknowledge(_office, _assignment, 2);
        Assert.Equal("creating", Assert.Single(Open().Discover(_office)).Phase);
        Assert.False(journal.TryBegin(_office, _assignment, 2, "test"));
    }

    [Theory]
    [InlineData(IsolationWorkloadState.Running)]
    [InlineData(IsolationWorkloadState.Stopping)]
    [InlineData(IsolationWorkloadState.Stopped)]
    [InlineData(IsolationWorkloadState.Failed)]
    public async Task DestroyRequiresConfirmedRemovalAndCanRecoverAfterProcessRestart(IsolationWorkloadState state)
    {
        var journal = Open();
        journal.TryBegin(_office, _assignment, 2, "test");
        journal.BeginCreate(journal.Read(_office, _assignment, 2));
        var handle = new IsolationWorkloadHandle("test", Guid.NewGuid(), "vm-1", WorkloadKind.Runtime);
        journal.Created(journal.Read(_office, _assignment, 2), handle);
        var provider = new Provider { State = state };
        var entry = Open().Read(_office, _assignment, 2);
        Assert.False(await journal.CleanupAsync(entry, provider, default));
        Assert.Equal("created", Open().Read(_office, _assignment, 2).Phase);
        provider.FailDestroy = true;
        await Assert.ThrowsAsync<IOException>(() => journal.CleanupAsync(entry, provider, default));
        provider.FailDestroy = false;
        provider.FailInspect = true;
        await Assert.ThrowsAsync<IOException>(() => journal.CleanupAsync(entry, provider, default));
        provider.FailInspect = false;
        provider.State = IsolationWorkloadState.Destroyed;
        provider.WrongHandle = true;
        Assert.False(await journal.CleanupAsync(entry, provider, default));
        provider.WrongHandle = false;
        journal = Open();
        Assert.True(await journal.CleanupAsync(journal.Read(_office, _assignment, 2), provider, default));
        Assert.Equal(handle, provider.LastHandle);
        Assert.Equal(handle, Assert.Single(Open().Discover(_office)).Handle);
        journal.Acknowledge(_office, _assignment, 2);
        Assert.False(Open().TryBegin(_office, _assignment, 2, "test"));
    }

    [Fact]
    public void DiscoveryIsBoundedAndRotatesPastUnresolvedEntries()
    {
        var journal = Open();
        for (var i = 1; i <= 20; i++) journal.TryBegin(_office, _assignment, i, "test");
        var seen = new HashSet<long>();
        for (var i = 0; i < 4; i++)
        {
            var batch = journal.Discover(_office);
            Assert.InRange(batch.Count, 0, 8);
            foreach (var entry in batch) seen.Add(entry.FencingEpoch);
        }
        Assert.Equal(20, seen.Count);
        Assert.Empty(journal.Discover(Guid.NewGuid()));
    }

    [Fact]
    public async Task EarlierUnconfirmedAttemptKeepsMaintenanceBlockedAfterRestart()
    {
        var journal = Open();
        journal.TryBegin(_office, _assignment, 2, "test");
        journal.BeginCreate(journal.Read(_office, _assignment, 2));
        journal.TryBegin(_office, _assignment, 3, "test");
        await journal.CleanupAsync(journal.Read(_office, _assignment, 3), null, default);
        Assert.True(journal.HasUnconfirmed(_office, _assignment));
        var state = new OfficeStateStore(new OfficeOptions { StateDirectory = _root });
        state.InitializeMaintenanceSession();
        Open().RestoreMaintenance(_office, state);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "maintenance", "active-assignments"), "*.active"));
        Assert.False(journal.HasUnconfirmed(Guid.NewGuid(), _assignment));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    private sealed class Provider : IAgentIsolationProvider
    {
        public IsolationWorkloadState State { get; set; }
        public bool FailDestroy { get; set; }
        public bool FailInspect { get; set; }
        public bool WrongHandle { get; set; }
        public IsolationWorkloadHandle? LastHandle { get; private set; }
        public IsolationProviderDescriptor Descriptor => new("test", "test", "1", "windows", "x64", 0, null!);
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
        {
            LastHandle = handle;
            return FailDestroy ? Task.FromException(new IOException("provider unavailable")) : Task.CompletedTask;
        }
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            FailInspect ? Task.FromException<IsolationWorkloadStatus?>(new IOException("inspection unavailable")) :
                Task.FromResult<IsolationWorkloadStatus?>(new(WrongHandle ? handle with { ProviderInstanceId = "wrong" } : handle,
                    State, IsolationTerminationReason.None, null, null, null, null, null));
        public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IsolationWorkloadHandle> CreateAsync(WorkloadSpecification workload, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
