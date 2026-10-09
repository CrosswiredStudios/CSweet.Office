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
    }
}
