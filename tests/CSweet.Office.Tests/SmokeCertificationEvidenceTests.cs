using System.Text.Json;

namespace CSweet.Office.Tests;

public sealed class SmokeCertificationEvidenceTests
{
    [Fact]
    public void NonExpiringEvidenceIncludesExplicitNullForStrictModeInstallers()
    {
        var evidence = new
        {
            certifiedAt = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero),
            certificationExpiresAt = (DateTimeOffset?)null,
            checks = new Dictionary<string, bool> { ["outbound-network-denied"] = true }
        };

        using var json = JsonDocument.Parse(SmokeJson.SerializeCertificationEvidence(evidence));

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("certificationExpiresAt").ValueKind);
        Assert.Equal(evidence.certifiedAt, json.RootElement.GetProperty("certifiedAt").GetDateTimeOffset());
        Assert.True(json.RootElement.GetProperty("checks").GetProperty("outbound-network-denied").GetBoolean());
    }
}
