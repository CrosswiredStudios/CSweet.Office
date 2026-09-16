namespace CSweet.Office.Tests;

public sealed class MaintenanceHandoffTests
{
    [Theory]
    [InlineData("repair", true)]
    [InlineData("upgrade", true)]
    [InlineData("remove", false)]
    [InlineData("powershell", false)]
    public void OnlyPredefinedMaintenanceActionsAreAccepted(string operation, bool expected)
    {
        var id = Guid.NewGuid();
        var value = $"csweet-office://enroll/v1?office={id:D}&operation={operation}&origin=https%3A%2F%2Fhq.test&session={Guid.NewGuid():D}#handoff=secret";
        Assert.Equal(expected, MaintenanceWorker.ValidHandoff(value, id, new Uri("https://hq.test")));
        Assert.False(MaintenanceWorker.ValidHandoff(value, Guid.NewGuid(), new Uri("https://hq.test")));
        Assert.False(MaintenanceWorker.ValidHandoff(value, id, new Uri("https://other.test")));
        Assert.False(MaintenanceWorker.ValidHandoff(value.Replace("https%3A", "http%3A"), id, new Uri("https://hq.test")));
    }

    [Theory]
    [InlineData("csweet-office://enroll/v1?broken#handoff=x")]
    [InlineData("csweet-office://enroll/v1?office=x&office=y#handoff=x")]
    [InlineData("https://hq.test")]
    public void MalformedHandoffsFailClosed(string value) =>
        Assert.False(MaintenanceWorker.ValidHandoff(value, Guid.NewGuid(), new Uri("https://hq.test")));
}
