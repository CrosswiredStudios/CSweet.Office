namespace CSweet.SatelliteOffice.Node;

public sealed class SatelliteOfficeOptions
{
    public const string SectionName = "CSweet:SatelliteOffice:Node";

    public string ControlPlaneUrl { get; set; } = "https://localhost:7443";
    public string ControlPlaneCertificateSha256 { get; set; } = string.Empty;
    public string ControlPlaneTrustFilePath { get; set; } = string.Empty;
    public string EnrollmentToken { get; set; } = string.Empty;
    public string EnrollmentTokenFilePath { get; set; } = string.Empty;
    public string StateDirectory { get; set; } = string.Empty;
    public string ArtifactCacheDirectory { get; set; } = string.Empty;
    public string ArtifactMediaDirectory { get; set; } = string.Empty;
    public string SatelliteOfficeName { get; set; } = Environment.MachineName;
    public int AllocatableCpuCount { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);
    public int AllocatableMemoryMb { get; set; } = 4096;
    public int AllocatableDiskMb { get; set; } = 32768;
    public int MaximumConcurrentWorkloads { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public string SecurityProfile { get; set; } = "baseline";
    public bool MixedUseHost { get; set; } = true;
    public bool AllowDevelopmentAssignments { get; set; }
    public string[] EnabledSecurityControls { get; set; } = [];
    public string[] MissingSecurityControls { get; set; } = [];

    public SatelliteOfficeSecurityPostureReport SecurityPosture()
    {
        var profile = SecurityProfile.Trim().ToLowerInvariant();
        if (profile is not ("baseline" or "hardened" or "development"))
            throw new InvalidOperationException("SecurityProfile must be baseline, hardened, or development.");
        if (profile == "development" && !AllowDevelopmentAssignments)
            throw new InvalidOperationException(
                "The development security profile requires explicit AllowDevelopmentAssignments consent.");
        if (profile == "hardened" && MissingSecurityControls.Length != 0)
            throw new InvalidOperationException(
                "The hardened security profile cannot be reported while required controls are missing.");
        return new SatelliteOfficeSecurityPostureReport(
            profile, MixedUseHost, AllowDevelopmentAssignments,
            NormalizeControls(EnabledSecurityControls), NormalizeControls(MissingSecurityControls),
            DateTimeOffset.UtcNow);
    }

    public string ResolveStateDirectory()
    {
        if (!string.IsNullOrWhiteSpace(StateDirectory)) return Path.GetFullPath(StateDirectory);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(local) ? AppContext.BaseDirectory : local,
            "CSweet", "SatelliteOffice");
    }

    public string ResolveArtifactCacheDirectory() => string.IsNullOrWhiteSpace(ArtifactCacheDirectory)
        ? Path.Combine(ResolveStateDirectory(), "artifact-cache")
        : Path.GetFullPath(ArtifactCacheDirectory);

    public string ResolveArtifactMediaDirectory() => string.IsNullOrWhiteSpace(ArtifactMediaDirectory)
        ? Path.Combine(ResolveStateDirectory(), "artifact-media")
        : Path.GetFullPath(ArtifactMediaDirectory);

    private static string[] NormalizeControls(IEnumerable<string> controls) => controls
        .Select(value => value.Trim().ToLowerInvariant())
        .Where(value => value.Length is > 0 and <= 100 &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.'))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Take(64)
        .ToArray();
}
