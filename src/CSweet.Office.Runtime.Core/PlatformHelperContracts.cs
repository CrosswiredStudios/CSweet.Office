using CSweet.Office.Runtime.Abstractions;

namespace CSweet.Office.Runtime.Core;

public sealed class PlatformHelperRequest
{
    public BuilderWorkloadSpecification? BuilderWorkload { get; set; }
    public RuntimeWorkloadSpecification? RuntimeWorkload { get; set; }
    public ToolchainBuildWorkloadSpecification? ToolchainBuildWorkload { get; set; }
    public IsolationWorkloadHandle? Handle { get; set; }
    public string? GuestImagePath { get; set; }
    public string? ArtifactImagePath { get; set; }
    public int? GracePeriodSeconds { get; set; }
    public int? MaximumBytes { get; set; }
}

public sealed class PlatformHelperResponse
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? SanitizedError { get; set; }
    public string? ProviderInstanceId { get; set; }
    public IsolationWorkloadStatus? Status { get; set; }
    public IReadOnlyList<IsolationLogChunk>? Logs { get; set; }
    public int WorkloadsRemoved { get; set; }
    public string? GuestChannelTransport { get; set; }
}
