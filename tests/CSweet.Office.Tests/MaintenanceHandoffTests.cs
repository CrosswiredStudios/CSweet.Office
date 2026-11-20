namespace CSweet.Office.Tests;

public sealed class MaintenanceHandoffTests
{
    [Theory]
    [InlineData("", false, true)]
    [InlineData("", true, false)]
    [InlineData("&operation=repair", false, false)]
    [InlineData("&office=146548c3-ad6c-4a93-9451-456f68990abb&operation=upgrade", true, false)]
    [InlineData("&office=146548c3-ad6c-4a93-9451-456f68990abb&operation=repair", true, false)]
    [InlineData("&automatic=true", false, false)]
    [InlineData("&localSource=true", false, false)]
    [InlineData("&office=146548c3-ad6c-4a93-9451-456f68990abb&operation=remove", true, true)]
    public void SavedMaintenanceLinksNeverRequestInteractiveElevation(string extra, bool installed, bool allowed)
    {
        var input = Arguments.Uri([$"csweet-office://enroll/v1?session={Guid.NewGuid():D}&origin=https%3A%2F%2Fhq.test{extra}#handoff=opaque"]);
        Assert.NotNull(input);
        Assert.True(Arguments.TryReadHandoff(input, out var query, out _, out _, out _));
        Assert.Equal(allowed, Arguments.AllowInteractiveLaunch(query, () => installed));
    }

    [Theory]
    [InlineData("origin=http%3A%2F%2Fhq.test#handoff=valid")]
    [InlineData("origin=https%3A%2F%2Fhq.test&origin=https%3A%2F%2Fother.test#handoff=valid")]
    [InlineData("origin=https%3A%2F%2Fhq.test#other=valid")]
    [InlineData("origin=https%3A%2F%2Fhq.test#handoff=invalid%20token")]
    public void MalformedProtocolLinksAreRejectedBeforeElevation(string query)
    {
        var input = Arguments.Uri([$"csweet-office://enroll/v1?session={Guid.NewGuid():D}&{query}"]);
        Assert.NotNull(input);
        Assert.False(Arguments.TryReadHandoff(input, out _, out _, out _, out _));
    }

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
        Assert.False(MaintenanceWorker.ValidHandoff(value.Replace("#handoff=", "&localSource=true#handoff="), id, new Uri("https://hq.test")));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("b", false)]
    [InlineData("a", true)]
    public void ConfiguratorCanOnlyUseTheInstalledTlsPin(string? suffix, bool valid)
    {
        var office = Guid.NewGuid();
        var pin = new string('a', 64);
        var parameter = suffix is null ? "" : "&certificate=" + new string(suffix[0], 64);
        var uri = $"csweet-office://enroll/v1?office={office:D}&operation=repair&origin=https%3A%2F%2Fhq.test&session={Guid.NewGuid():D}{parameter}#handoff=secret";
        Assert.Equal(valid, MaintenanceWorker.ValidHandoff(uri, office, new Uri("https://hq.test"), pin));
    }

    [Theory]
    [InlineData("csweet-office://enroll/v1?broken#handoff=x")]
    [InlineData("csweet-office://enroll/v1?office=x&office=y#handoff=x")]
    [InlineData("https://hq.test")]
    public void MalformedHandoffsFailClosed(string value) =>
        Assert.False(MaintenanceWorker.ValidHandoff(value, Guid.NewGuid(), new Uri("https://hq.test")));
}
