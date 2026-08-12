using CSweet.SatelliteOffice.Node;

namespace CSweet.SatelliteOffice.Tests;

public sealed class SatelliteOfficeMaintenanceStateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"csweet-node-maintenance-{Guid.NewGuid():N}");

    [Fact]
    public void DrainStateIsDurableAndResumable()
    {
        var store = Store();

        store.SetDraining(true);
        Assert.Equal("draining", File.ReadAllText(
            Path.Combine(_root, "maintenance", "drain-state")));

        store.SetDraining(false);
        Assert.Equal("ready", File.ReadAllText(
            Path.Combine(_root, "maintenance", "drain-state")));
    }

    [Fact]
    public void AssignmentMarkersTrackWorkAndAreClearedForANewProcessSession()
    {
        var store = Store();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        store.MarkAssignmentActive(first);
        store.MarkAssignmentActive(second);
        var active = Path.Combine(_root, "maintenance", "active-assignments");
        Assert.Equal(2, Directory.GetFiles(active, "*.active").Length);

        store.MarkAssignmentInactive(first);
        Assert.Single(Directory.GetFiles(active, "*.active"));

        store.InitializeMaintenanceSession();
        Assert.Empty(Directory.GetFiles(active, "*.active"));
    }

    private SatelliteOfficeStateStore Store() => new(new SatelliteOfficeOptions
    {
        StateDirectory = _root
    });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }
}
