using CSweet.SatelliteOffice.Runtime.Abstractions;
using CSweet.SatelliteOffice.Runtime.HyperV.Helper;

namespace CSweet.SatelliteOffice.Tests;

public sealed class HyperVInstanceReapingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RuntimeWithActiveLeaseAndRunningVm_IsRetained()
    {
        var metadata = Metadata(WorkloadKind.Runtime, Now.AddMinutes(1));

        Assert.False(HyperVHelperController.ShouldReap(metadata, "Running", Now));
    }

    [Fact]
    public void RuntimeWithExpiredLease_IsReaped()
    {
        var metadata = Metadata(WorkloadKind.Runtime, Now.AddSeconds(-1));

        Assert.True(HyperVHelperController.ShouldReap(metadata, "Running", Now));
    }

    [Fact]
    public void PoweredOffRuntime_IsReapedBeforeLeaseExpiry()
    {
        var metadata = Metadata(WorkloadKind.Runtime, Now.AddHours(1));

        Assert.True(HyperVHelperController.ShouldReap(metadata, "Off", Now));
    }

    [Fact]
    public void NewlyCreatedRuntime_IsNotReapedBeforeItCanStart()
    {
        var metadata = Metadata(WorkloadKind.Runtime, Now.AddHours(1)) with
        {
            CreatedAt = Now,
            StartedAt = null
        };

        Assert.False(HyperVHelperController.ShouldReap(metadata, "Off", Now));
    }

    [Fact]
    public void LegacyRuntimeWithoutLease_IsReapedFailClosed()
    {
        var metadata = Metadata(WorkloadKind.Runtime, null);

        Assert.True(HyperVHelperController.ShouldReap(metadata, "Running", Now));
    }

    [Fact]
    public void BuilderIsNeverReapedByRuntimeSweep()
    {
        var metadata = Metadata(WorkloadKind.Builder, Now.AddSeconds(-1));

        Assert.False(HyperVHelperController.ShouldReap(metadata, "Off", Now));
    }

    private static HyperVInstanceMetadata Metadata(WorkloadKind kind, DateTimeOffset? expiresAt) =>
        new(Guid.NewGuid(), Guid.NewGuid(), kind, $"CSweet-{kind}-{Guid.NewGuid():N}",
            Now.AddHours(-1), Now.AddMinutes(-59), null, expiresAt);
}
