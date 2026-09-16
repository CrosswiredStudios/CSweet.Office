using CSweet.Office.RuntimeGuest;
namespace CSweet.Office.Tests;
public sealed class WorkspaceEnvironmentTests
{
    [Theory]
    [InlineData("CSWEET_WORKSPACE_MAXIMUM_ARCHIVE_BYTES")]
    [InlineData("CSWEET_WORKSPACE_MAXIMUM_EXPANDED_BYTES")]
    [InlineData("CSWEET_WORKSPACE_MAXIMUM_FILE_COUNT")]
    public void AcceptsPlatformWorkspaceLimits(string key) =>
        Assert.True(GuestWorkloadSupervisor.IsAllowedEnvironmentKey(key));
    [Theory]
    [InlineData("PATH")]
    [InlineData("LD_PRELOAD")]
    [InlineData("CSWEET_WORKSPACE_ARBITRARY")]
    public void RejectsUnapprovedEnvironmentKeys(string key) =>
        Assert.False(GuestWorkloadSupervisor.IsAllowedEnvironmentKey(key));
}
