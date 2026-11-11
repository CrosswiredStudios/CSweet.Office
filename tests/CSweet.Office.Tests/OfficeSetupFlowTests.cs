using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Office.Node;
using CSweet.Office.Runtime.Core;

namespace CSweet.Office.Tests;

/// <summary>Guided-setup behaviour fixed in 0.14.1: enrollment timing, rejected tokens and install speed.</summary>
public sealed class OfficeSetupFlowTests
{
    [Theory]
    [InlineData("invalid_enrollment", true)]
    [InlineData("invalid_node_claim", false)]
    [InlineData(null, false)]
    public void OnlyAHeadquartersRejectionStopsEnrollmentRetries(string? errorCode, bool expected) =>
        Assert.Equal(expected, OfficeWorker.IsDefinitiveEnrollmentRejection(errorCode));

    [Fact]
    public async Task RejectedTokenIsDeletedAndMarkedSoItIsNeverClaimedAgain()
    {
        var root = Directory.CreateTempSubdirectory("office-enrollment-").FullName;
        try
        {
            var token = Path.Combine(root, "enrollment.secret");
            await File.WriteAllTextAsync(token, "rejected-token");
            Assert.False(OfficeWorker.IsAwaitingNewEnrollment(root));

            await OfficeWorker.RecordEnrollmentRejectionAsync(root, token, "invalid_enrollment",
                new DateTimeOffset(2026, 11, 5, 2, 53, 47, TimeSpan.Zero), CancellationToken.None);

            Assert.False(File.Exists(token));
            Assert.True(OfficeWorker.IsAwaitingNewEnrollment(root));
            using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(root, OfficeWorker.EnrollmentRejectedFileName)));
            Assert.Equal("enrollment-rejected", marker.RootElement.GetProperty("state").GetString());
            Assert.Equal("invalid_enrollment", marker.RootElement.GetProperty("errorCode").GetString());
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProbeVerifiesTheGuestImageDigestFromOneHash()
    {
        var hash = SHA256.HashData("guest-image"u8);
        Assert.True(ExternalPlatformIsolationBackend.DigestMatches(hash, $"sha256:{Convert.ToHexStringLower(hash)}"));
        Assert.False(ExternalPlatformIsolationBackend.DigestMatches(hash, $"sha256:{new string('0', 64)}"));
        Assert.False(ExternalPlatformIsolationBackend.DigestMatches(hash[..16], $"sha256:{Convert.ToHexStringLower(hash)}"));

        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "CSweet.Office.Runtime.Core",
            "ExternalPlatformIsolationBackend.cs"));
        var probe = source[source.IndexOf("public async Task<IsolationProviderProbeResult> ProbeAsync", StringComparison.Ordinal)..];
        probe = probe[..probe.IndexOf("PlatformHelperResponse response;", StringComparison.Ordinal)];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(probe, "HashFileAsync\\(guestImage"));
        Assert.DoesNotContain("VerifyFileDigestAsync(guestImage", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeLogsTheRejectionTheInstallerFailsFastOn()
    {
        var worker = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "CSweet.Office.Node", "OfficeWorker.cs"));
        Assert.Contains("$\"Office enrollment failed ({result.ErrorCode}): {result.Message}\"", worker, StringComparison.Ordinal);
        Assert.Contains("catch (OfficeEnrollmentAwaitingTokenException)", worker, StringComparison.Ordinal);
        Assert.True(worker.IndexOf("IsAwaitingNewEnrollment(options.ResolveStateDirectory())", StringComparison.Ordinal) <
            worker.IndexOf("inventory.ProbeAsync(cancellationToken)", StringComparison.Ordinal),
            "A rejected token must stop before the slow provider probe and any new claim.");
    }

    [Fact]
    public void GuidedSetupReusesHostCertificationAndBudgetsImageBuildCpus()
    {
        var root = RepositoryRoot();
        var bootstrap = File.ReadAllText(Path.Combine(root, "scripts", "windows", "Initialize-CSweetWindowsIsolationTest.ps1"));
        Assert.Contains("Get-CSweetReusableCertification", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Save-CSweetReusableCertification", bootstrap, StringComparison.Ordinal);
        Assert.Contains("[switch] $Recertify", bootstrap, StringComparison.Ordinal);
        Assert.Contains("$imageHash = $guestImageSha256", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Choose Continue setup", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reopen C-Sweet afterward to resume", bootstrap, StringComparison.Ordinal);
        // The cache is saved only after the evidence passed every check.
        Assert.True(bootstrap.IndexOf("Certification evidence contains a failed isolation check.", StringComparison.Ordinal) <
            bootstrap.IndexOf("Save-CSweetReusableCertification -CacheRoot", StringComparison.Ordinal));

        var guest = File.ReadAllText(Path.Combine(root, "scripts", "windows", "New-CSweetHyperVTestGuest.ps1"));
        Assert.Contains("$env:PKR_VAR_build_cpus = [string][Math]::Min(4, $BuildCpuCount)", guest, StringComparison.Ordinal);
        var packer = File.ReadAllText(Path.Combine(root, "build", "windows-hyperv", "csweet-agent-guest.pkr.hcl"));
        Assert.Contains("cpus                             = var.build_cpus", packer, StringComparison.Ordinal);
        var hosted = File.ReadAllText(Path.Combine(root, "scripts", "release", "build-hosted-hyperv-image.sh"));
        Assert.Contains("--smp \"$build_cpus\"", hosted, StringComparison.Ordinal);
        Assert.Contains("(( build_cpus > 4 )) && build_cpus=4", hosted, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.Office.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The C-Sweet Office repository root was not found.");
    }
}
