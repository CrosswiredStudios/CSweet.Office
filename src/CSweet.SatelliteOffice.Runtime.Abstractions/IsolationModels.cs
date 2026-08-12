namespace CSweet.SatelliteOffice.Runtime.Abstractions;

public enum IsolationAssurance
{
    None = 0,
    Process = 10,
    SharedKernelContainer = 20,
    HardwareVirtualMachine = 30,
    CertifiedHardwareVirtualMachine = 40,
    RemoteCertifiedHardwareVirtualMachine = 50
}

public enum IsolationWorkloadState
{
    Creating,
    Created,
    Starting,
    BootstrappingGuest,
    Running,
    Stopping,
    Stopped,
    Destroying,
    Destroyed,
    Failed
}

public enum IsolationTerminationReason
{
    None,
    Completed,
    Cancelled,
    StartFailed,
    GuestBootstrapFailed,
    LeaseExpired,
    RuntimeLimitExceeded,
    ResourceLimitExceeded,
    PolicyDenied,
    SecurityViolation,
    ProviderFailure,
    HostShutdown
}

public sealed record IsolationProviderCapabilities(
    IsolationAssurance Assurance,
    bool UsesDedicatedKernel,
    bool SupportsBrokerSocket,
    bool SupportsReadOnlyBaseDisk,
    bool SupportsReadOnlyArtifact,
    bool SupportsEphemeralWritableDisk,
    bool SupportsCpuLimits,
    bool SupportsMemoryLimits,
    bool SupportsDiskLimits,
    bool SupportsProcessLimits,
    bool SupportsNoNetworkDevice,
    bool SupportsSecureBoot,
    bool SupportsMeasuredOrVerifiedBoot)
{
    public bool Satisfies(IsolationCapabilityRequirements requirements) =>
        Assurance >= requirements.MinimumAssurance &&
        (!requirements.RequireDedicatedKernel || UsesDedicatedKernel) &&
        (!requirements.RequireBrokerSocket || SupportsBrokerSocket) &&
        (!requirements.RequireReadOnlyBaseDisk || SupportsReadOnlyBaseDisk) &&
        (!requirements.RequireReadOnlyArtifact || SupportsReadOnlyArtifact) &&
        (!requirements.RequireEphemeralWritableDisk || SupportsEphemeralWritableDisk) &&
        (!requirements.RequireCpuLimits || SupportsCpuLimits) &&
        (!requirements.RequireMemoryLimits || SupportsMemoryLimits) &&
        (!requirements.RequireDiskLimits || SupportsDiskLimits) &&
        (!requirements.RequireNoNetworkDevice || SupportsNoNetworkDevice) &&
        (!requirements.RequireSecureBoot || SupportsSecureBoot) &&
        (!requirements.RequireMeasuredOrVerifiedBoot || SupportsMeasuredOrVerifiedBoot);
}

public sealed record IsolationCapabilityRequirements(
    IsolationAssurance MinimumAssurance,
    bool RequireDedicatedKernel = true,
    bool RequireBrokerSocket = true,
    bool RequireReadOnlyBaseDisk = true,
    bool RequireReadOnlyArtifact = true,
    bool RequireEphemeralWritableDisk = true,
    bool RequireCpuLimits = true,
    bool RequireMemoryLimits = true,
    bool RequireDiskLimits = true,
    bool RequireNoNetworkDevice = true,
    bool RequireSecureBoot = false,
    bool RequireMeasuredOrVerifiedBoot = false);

public sealed record IsolationProviderDescriptor(
    string ProviderId,
    string DisplayName,
    string ProviderVersion,
    string HostOperatingSystem,
    string HostArchitecture,
    int Priority,
    IsolationProviderCapabilities Capabilities);

public sealed record IsolationProviderCertification(
    string ProviderId,
    string ProviderVersion,
    string HostOperatingSystem,
    string HostArchitecture,
    string GuestImageDigest,
    string BrokerProtocolVersion,
    string CertificationSuiteVersion,
    string EvidenceDigest,
    DateTimeOffset CertifiedAt,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? RevokedAt = null)
{
    public bool IsActiveAt(DateTimeOffset instant) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > instant);
}

public sealed record IsolationProviderProbeResult(
    IsolationProviderDescriptor Descriptor,
    bool IsAvailable,
    string? UnavailableReason,
    IsolationProviderCertification? Certification);

public sealed record IsolationWorkloadHandle(
    string ProviderId,
    Guid WorkloadId,
    string ProviderInstanceId,
    WorkloadKind Kind);

public sealed record IsolationWorkloadStatus(
    IsolationWorkloadHandle Handle,
    IsolationWorkloadState State,
    IsolationTerminationReason TerminationReason,
    int? ExitCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorCode,
    string? SanitizedError);

public sealed record IsolationLogChunk(
    DateTimeOffset OccurredAt,
    string Stream,
    ReadOnlyMemory<byte> Content,
    bool IsTruncated);

public sealed record IsolationSelectionRequest(
    AgentTrustLevel TrustLevel,
    IsolationCapabilityRequirements Requirements,
    string? GuestImageDigest,
    string BrokerProtocolVersion,
    string? PreferredProviderId = null);

public sealed record IsolationProviderSelection(
    IAgentIsolationProvider Provider,
    IsolationProviderProbeResult Probe);
