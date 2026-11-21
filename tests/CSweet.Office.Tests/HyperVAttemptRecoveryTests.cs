using CSweet.Office.Runtime.Abstractions;
using CSweet.Office.Runtime.Core;
using CSweet.Office.Runtime.HyperV.Helper;

namespace CSweet.Office.Tests;

public sealed class HyperVAttemptRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hyperv-attempt-{Guid.NewGuid():N}");
    private HyperVHelperPaths Paths => new(_root, Path.Combine(_root, "instances"),
        Path.Combine(_root, "vm-config"), Path.Combine(_root, "media"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCreateRecoversExactVmAndRetainsReceiptAfterLostResponse(bool retained)
    {
        var key = Guid.NewGuid(); var workload = Guid.NewGuid(); var instance = Guid.NewGuid();
        using (var journal = new HyperVAttemptJournal(Paths, key))
            journal.Save(new(key, workload, WorkloadKind.Runtime, retained ? instance : null));
        Directory.CreateDirectory(Paths.InstanceDirectory(instance));
        var vm = new FakeVm { Instance = instance };
        var controller = new HyperVHelperController(Paths, vm);
        var request = new PlatformHelperRequest { AttemptKey = key, RecoveryWorkloadId = workload,
            RecoveryWorkloadKind = WorkloadKind.Runtime };
        var result = await controller.ExecuteAsync("destroy", request);
        Assert.True(result.Success); Assert.Equal(instance.ToString("N"), result.ProviderInstanceId);
        Assert.Equal($"CSweet-Runtime-{key:N}", vm.LastName);
        Assert.False(Directory.Exists(Paths.InstanceDirectory(instance)));
        var replay = await new HyperVHelperController(Paths, new FakeVm { Fail = true }).ExecuteAsync("destroy", request);
        Assert.True(replay.Success); Assert.Equal(result.ProviderInstanceId, replay.ProviderInstanceId);
        request.RecoveryWorkloadId = Guid.NewGuid();
        Assert.False((await controller.ExecuteAsync("destroy", request)).Success);
    }

    [Fact]
    public async Task MissingCreateIntentBecomesPermanentTombstoneOnlyAfterAuthoritativeInspection()
    {
        var key = Guid.NewGuid(); var workload = Guid.NewGuid();
        var request = new PlatformHelperRequest { AttemptKey = key, RecoveryWorkloadId = workload,
            RecoveryWorkloadKind = WorkloadKind.Runtime };
        var vm = new FakeVm { Fail = true };
        await Assert.ThrowsAsync<IOException>(() => new HyperVHelperController(Paths, vm).ExecuteAsync("destroy", request));
        using (var journal = new HyperVAttemptJournal(Paths, key)) Assert.Null(journal.Read());
        vm.Fail = false;
        var result = await new HyperVHelperController(Paths, vm).ExecuteAsync("destroy", request);
        Assert.True(result.Success); Assert.Null(result.ProviderInstanceId);
        using var reopened = new HyperVAttemptJournal(Paths, key);
        Assert.True(reopened.Read()!.Stopped);
    }

    [Fact]
    public async Task InspectRequiresCompletedMetadataAndTheOriginalIsolatedVmLayout()
    {
        var key = Guid.NewGuid();
        var instance = Guid.NewGuid();
        var workload = Guid.NewGuid();
        var attempt = new HyperVAttempt(key, workload, WorkloadKind.Runtime, instance);
        var request = new PlatformHelperRequest { AttemptKey = key, RecoveryWorkloadId = workload,
            RecoveryWorkloadKind = WorkloadKind.Runtime };
        var hyperv = new FakeVm { Instance = instance };
        var controller = new HyperVHelperController(Paths, hyperv);
        Assert.False((await controller.ExecuteAsync("inspect", request)).Success);
        using (var journal = new HyperVAttemptJournal(Paths, key)) journal.Save(attempt);
        Assert.False((await controller.ExecuteAsync("inspect", request)).Success);
        var directory = Directory.CreateDirectory(Paths.InstanceDirectory(instance)).FullName;
        var now = DateTimeOffset.UtcNow;
        var metadata = new HyperVInstanceMetadata(instance, workload, WorkloadKind.Runtime, attempt.VmName,
            now, null, null, now.AddMinutes(5));
        File.WriteAllText(Path.Combine(directory, "instance.json"), System.Text.Json.JsonSerializer.Serialize(metadata));
        Assert.Equal(instance.ToString("N"), (await controller.ExecuteAsync("inspect", request)).ProviderInstanceId);
        Assert.Equal(attempt.VmName, hyperv.LastName);
        hyperv.Instance = Guid.NewGuid();
        Assert.False((await controller.ExecuteAsync("inspect", request)).Success);
        hyperv.Instance = instance;
        using (var journal = new HyperVAttemptJournal(Paths, key)) journal.Save(attempt with { Stopped = true });
        Assert.False((await controller.ExecuteAsync("inspect", request)).Success);
        Assert.Equal(0, hyperv.Destroys);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task UnconfirmedRemovalAndChangedVmIdentityDoNotProduceReceipts()
    {
        var key = Guid.NewGuid(); var instance = Guid.NewGuid(); var workload = Guid.NewGuid();
        using (var journal = new HyperVAttemptJournal(Paths, key))
            journal.Save(new(key, workload, WorkloadKind.Runtime, instance));
        var request = new PlatformHelperRequest { AttemptKey = key, RecoveryWorkloadId = workload,
            RecoveryWorkloadKind = WorkloadKind.Runtime };
        var vm = new FakeVm { Instance = Guid.NewGuid() };
        Assert.False((await new HyperVHelperController(Paths, vm).ExecuteAsync("destroy", request)).Success);
        Assert.Equal(0, vm.Destroys);
        vm.Instance = instance; vm.Preserve = true;
        Assert.False((await new HyperVHelperController(Paths, vm).ExecuteAsync("destroy", request)).Success);
        using var reopened = new HyperVAttemptJournal(Paths, key);
        Assert.False(reopened.Read()!.Stopped);
        // Provider-owned lock also prevents recovery while a create invocation owns its journal.
        await Assert.ThrowsAsync<IOException>(() => new HyperVHelperController(Paths, vm).ExecuteAsync("destroy", request));
    }

    [Fact]
    public async Task Compute_teardown_replay_observes_the_original_tombstone_and_disk_without_mutating()
    {
        var id = Guid.NewGuid();
        var physical = Guid.NewGuid();
        var request = new PlatformHelperRequest { AttemptKey = id, RecoveryWorkloadId = id,
            RecoveryWorkloadKind = WorkloadKind.ComputeEnvironment };
        var vm = new FakeVm { Instance = physical };
        var helper = new HyperVHelperController(Paths, vm);
        Assert.False((await helper.ExecuteAsync("inspect-destruction", request)).Success);
        using (var journal = new HyperVAttemptJournal(Paths, id))
            journal.Save(new(id, id, WorkloadKind.ComputeEnvironment, physical));
        Assert.True((await helper.ExecuteAsync("destroy", request)).Success);
        var restarted = new HyperVHelperController(Paths, vm);
        var observed = await restarted.ExecuteAsync("inspect-destruction", request);
        Assert.True(observed.Success);
        Assert.Equal(physical.ToString("N"), observed.ProviderInstanceId);
        vm.Instance = Guid.NewGuid();
        Assert.False((await restarted.ExecuteAsync("inspect-destruction", request)).Success);
        vm.Instance = null;
        Directory.CreateDirectory(Paths.InstanceDirectory(physical));
        Assert.False((await restarted.ExecuteAsync("inspect-destruction", request)).Success);
        Assert.True(Directory.Exists(Paths.InstanceDirectory(physical)));
        Assert.Equal(1, vm.Destroys);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class FakeVm : IHyperVAttemptOperations
    {
        public Guid? Instance { get; set; }
        public bool Fail { get; set; }
        public bool Preserve { get; set; }
        public int Destroys { get; private set; }
        public string? LastName { get; private set; }
        public Task<Guid?> FindAsync(string vmName)
        { LastName = vmName; return Fail ? Task.FromException<Guid?>(new IOException("Hyper-V unavailable")) : Task.FromResult(Instance); }
        public Task DestroyAsync(string vmName) { Destroys++; if (!Preserve) Instance = null; return Task.CompletedTask; }
        public Task<bool> MatchesOwnedLayoutAsync(string name, Guid id, string instanceDirectory)
        {
            LastName = name;
            return Task.FromResult(Instance == id && Path.GetFileName(instanceDirectory) == id.ToString("N"));
        }
    }
}
