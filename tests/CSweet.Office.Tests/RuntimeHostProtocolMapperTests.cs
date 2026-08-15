using CSweet.Office.Runtime.Abstractions;
using CSweet.Office.Runtime.LocalRpc;

namespace CSweet.Office.Tests;

public sealed class RuntimeHostProtocolMapperTests
{
    private const string GuestDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ArtifactDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void RuntimeRoundTrip_PreservesBoundedSpec()
    {
        var expected = RuntimeSpec();

        var protocol = RuntimeHostProtocolMapper.ToProtocol("hyperv", expected);
        var actual = Assert.IsType<RuntimeWorkloadSpecification>(RuntimeHostProtocolMapper.FromProtocol(protocol));

        Assert.Equal(expected.WorkloadId, actual.WorkloadId);
        Assert.Equal(expected.GuestImage, actual.GuestImage);
        Assert.Equal(expected.ResourceLimits, actual.ResourceLimits);
        Assert.Equal(expected.BrokerLease.ChannelId, actual.BrokerLease.ChannelId);
        Assert.Equal(expected.BrokerLease.ProtocolVersion, actual.BrokerLease.ProtocolVersion);
        Assert.Equal(expected.BrokerLease.BootToken, actual.BrokerLease.BootToken);
        Assert.Equal(expected.BrokerLease.ExpectedGuestImageDigest, actual.BrokerLease.ExpectedGuestImageDigest);
        Assert.Equal(expected.BrokerLease.ExpectedArtifactDigest, actual.BrokerLease.ExpectedArtifactDigest);
        Assert.Equal(expected.BrokerLease.ExpiresAt.ToUnixTimeSeconds(), actual.BrokerLease.ExpiresAt.ToUnixTimeSeconds());
        Assert.Equal(expected.Artifact, actual.Artifact);
        Assert.Equal(expected.Entrypoint, actual.Entrypoint);
    }

    [Fact]
    public void ToProtocol_RejectsMismatchedArtifactBinding()
    {
        var workload = RuntimeSpec() with
        {
            BrokerLease = RuntimeSpec().BrokerLease with
            {
                ExpectedArtifactDigest = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
            }
        };

        Assert.Throws<ArgumentException>(() => RuntimeHostProtocolMapper.ToProtocol("hyperv", workload));
    }

    [Fact]
    public void FromProtocol_RejectsRepositoryCredentials()
    {
        var builder = BuilderSpec();
        var protocol = RuntimeHostProtocolMapper.ToProtocol("hyperv", builder);
        protocol.Builder.RepositoryUrl = "https://token@example.test/repository.git";

        Assert.Throws<InvalidDataException>(() => RuntimeHostProtocolMapper.FromProtocol(protocol));
    }

    private static RuntimeWorkloadSpecification RuntimeSpec()
    {
        var id = Guid.NewGuid();
        return new RuntimeWorkloadSpecification(
            id,
            Image(),
            Limits(),
            Lease(id, ArtifactDigest),
            new AgentArtifactReference(ArtifactDigest, "signature", "1.0", "linux", "x64"),
            new RuntimeAgentIdentity(Guid.NewGuid(), Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            ["/app/agent"]);
    }

    private static BuilderWorkloadSpecification BuilderSpec()
    {
        var id = Guid.NewGuid();
        return new BuilderWorkloadSpecification(
            id,
            Image(),
            Limits(),
            Lease(id, null),
            new RepositoryDescriptor(
                "https://example.test/repository.git",
                new string('a', 40),
                false,
                "dotnet-vm-v1",
                "1.0"),
            100 * 1024 * 1024);
    }

    private static GuestImageReference Image() => new("runtime", "1.0", GuestDigest, "linux", "x64");

    private static WorkloadResourceLimits Limits() => new(2, 200, 2048, 2048, 100, 1024 * 1024, TimeSpan.FromMinutes(10));

    private static BrokerChannelLease Lease(Guid id, string? artifactDigest) =>
        new(id, "1.0", "boot-token", GuestDigest, artifactDigest, DateTimeOffset.UtcNow.AddMinutes(5));
}
