using System.Security.Cryptography;
using CSweet.SatelliteOffice.Node;
using CSweet.SatelliteOffice.Runtime.LocalRpc;
using CSweet.SatelliteOffice.Runtime.Protocol;
using Xunit;

namespace CSweet.SatelliteOffice.Tests;

public sealed class SatelliteOfficeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"csweet-satellite-office-{Guid.NewGuid():N}");

    [Fact]
    public void LocalEndpointUsesBrandedV1Identity()
    {
        var endpoint = new RuntimeHostEndpointOptions();
        endpoint.Validate();
        Assert.Equal("CSweet:SatelliteOffice:RuntimeHost", RuntimeHostEndpointOptions.SectionName);
        Assert.Equal("csweet-satellite-office-runtime-v1", endpoint.NamedPipeName);
        Assert.Contains("csweet-satellite-office-runtime-v1.sock", endpoint.UnixSocketPath);
    }

    [Fact]
    public void RuntimeHostAuthenticationAcceptsSignedEnvelopeOnlyOnce()
    {
        var authenticator = new RuntimeHostRequestAuthenticator(new RuntimeHostAuthenticationOptions
        {
            KeyId = "test",
            SharedKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        }, TimeProvider.System);
        var envelope = new RuntimeHostEnvelope
        {
            ProtocolVersion = "1.0",
            RequestId = Guid.NewGuid().ToString("N"),
            ProbeRequest = new ProbeRequest { ProviderId = "hyperv" }
        };

        authenticator.Sign(envelope);

        Assert.True(authenticator.Validate(envelope).Accepted);
        Assert.Equal("replayed-request", authenticator.Validate(envelope).ErrorCode);
    }

    [Fact]
    public void WorkloadMapperRoundTripsSharedContract()
    {
        const string guestDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string artifactDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var id = Guid.NewGuid();
        var expected = new RuntimeWorkloadSpecification(
            id,
            new GuestImageReference("runtime", "1.0", guestDigest, "linux", "x64"),
            new WorkloadResourceLimits(2, 200, 2048, 2048, 100, 1024 * 1024, TimeSpan.FromMinutes(10)),
            new BrokerChannelLease(id, "1.0", "boot-token", guestDigest, artifactDigest, DateTimeOffset.UtcNow.AddMinutes(5)),
            new AgentArtifactReference(artifactDigest, "signature", "1.0", "linux", "x64"),
            new RuntimeAgentIdentity(Guid.NewGuid(), "agent", Guid.NewGuid()),
            ["/app/agent"]);

        var actual = Assert.IsType<RuntimeWorkloadSpecification>(
            RuntimeHostProtocolMapper.FromProtocol(RuntimeHostProtocolMapper.ToProtocol("hyperv", expected)));

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
        Assert.Equal(expected.Identity, actual.Identity);
        Assert.Equal(expected.Entrypoint, actual.Entrypoint);
    }

    [Fact]
    public void DrainAndActiveAssignmentMarkersAreDurable()
    {
        var store = new SatelliteOfficeStateStore(new SatelliteOfficeOptions { StateDirectory = _root });
        var assignment = Guid.NewGuid();
        store.SetDraining(true);
        store.MarkAssignmentActive(assignment);

        Assert.Equal("draining", File.ReadAllText(Path.Combine(_root, "maintenance", "drain-state")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "maintenance", "active-assignments"), "*.active"));

        store.MarkAssignmentInactive(assignment);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "maintenance", "active-assignments"), "*.active"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }
}
