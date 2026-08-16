using CSweet.Office.Contracts.Workloads;
using CSweet.Office.Runtime.HyperV.Helper;

namespace CSweet.Office.Tests;

public sealed class HyperVVmNamingTests
{
    [Fact]
    public void RuntimeVmName_IncludesSanitizedRoleNameAndDisplayName()
    {
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var workload = Runtime("Software Developer / R&D", "Ada O'Connor");

        var name = HyperVHelperController.BuildVmName(workload, id);

        Assert.Equal(
            "CSweet-Runtime-Software-Developer-R-D-Ada-O-Connor-12345678123412341234123456789abc",
            name);
        Assert.True(name.Length <= 100);
    }

    [Fact]
    public void RuntimeVmName_WithoutEmployeeIdentity_PreservesLegacyFormat()
    {
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");

        var name = HyperVHelperController.BuildVmName(Runtime(null, null), id);

        Assert.Equal("CSweet-Runtime-12345678123412341234123456789abc", name);
    }

    [Fact]
    public void RuntimeVmName_BoundsLongIdentitySegmentsAndRetainsUniqueId()
    {
        var id = Guid.NewGuid();

        var name = HyperVHelperController.BuildVmName(
            Runtime(new string('R', 200), new string('N', 200)), id);

        Assert.True(name.Length <= 100);
        Assert.EndsWith(id.ToString("N"), name, StringComparison.Ordinal);
    }

    private static RuntimeWorkloadSpecification Runtime(string? role, string? name)
    {
        var id = Guid.NewGuid();
        var guestDigest = "sha256:" + new string('a', 64);
        var artifactDigest = "sha256:" + new string('b', 64);
        return new RuntimeWorkloadSpecification(
            id,
            new GuestImageReference("runtime", "1.0", guestDigest, "linux", "x64"),
            new WorkloadResourceLimits(1, 100, 1024, 1024, 10, 1024, TimeSpan.FromMinutes(1)),
            new BrokerChannelLease(Guid.NewGuid(), "1.0", "token", guestDigest, artifactDigest, DateTimeOffset.UtcNow.AddMinutes(2)),
            new AgentArtifactReference(artifactDigest, "signature", "1.0", "linux", "x64"),
            new RuntimeAgentIdentity(Guid.NewGuid(), "business", Guid.NewGuid(), name, role),
            ["/app/agent"]);
    }
}
