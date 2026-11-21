using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using CSweet.Office.Contracts.ControlPlane;
using CSweet.Office.Contracts.Security;
using CSweet.Office.Runtime.Abstractions;
using CSweet.Office.Runtime.Core;
using CSweet.Office.Runtime.HyperV;
using CSweet.Office.Runtime.LocalRpc;
using CSweet.Office.Runtime.Protocol;
using Google.Protobuf;

namespace CSweet.Office.RuntimeHost;

public sealed class OfficeComputeOptions
{
    public bool Enabled { get; set; }
    public Guid OrganizationId { get; set; }
    public int MaximumEnvironments { get; set; }
    public int CpuCount { get; set; }
    public int MemoryMiB { get; set; }
    public int DiskMiB { get; set; }
    public string ImagePath { get; set; } = "";
    public string ImageDigest { get; set; } = "";
    public string SignaturePath { get; set; } = "";
    public string[] Features { get; set; } = [];
}

internal sealed class OfficeComputePartition(OfficeComputeOptions policy, HyperVIsolationBackendOptions runtime,
    RuntimeHostAuthorizationOptions storage, RuntimeHostAuthorizationGate trust, TimeProvider clock,
    ILogger<OfficeComputePartition> logger) : BackgroundService, IOfficeComputePartition
{
    private const string Provider = "office-hyperv";
    private static readonly HashSet<string> Actions = [InfrastructureActions.Provision, InfrastructureActions.Execute,
        InfrastructureActions.Start, InfrastructureActions.Stop, InfrastructureActions.Restart, InfrastructureActions.Destroy];
    private readonly SemaphoreSlim initialization = new(1);
    private readonly WindowsHyperVSocketTransport socket = new(new() { LinuxVsockPort = ComputeGuestWire.Port });
    private State? state;
    private sealed record State(PinnedHeadquartersTrust Trust, ComputeTemplate Template, HyperVIsolationBackend Backend,
        ComputeReplayJournal Journal, ComputeDispatchVerifier Verifier);

    private async Task<State?> OpenAsync(CancellationToken token)
    {
        if (policy.OrganizationId == Guid.Empty || !OperatingSystem.IsWindows()) return null;
        await initialization.WaitAsync(token);
        try
        {
            if (state is not null) return state;
            var pin = trust.ReadTrust();
            if (pin is null || pin.OfficeId == Guid.Empty) return null;
            var capacity = new ComputeProviderCapacity(policy.MaximumEnvironments, new(policy.CpuCount, policy.MemoryMiB, policy.DiskMiB));
            if (!capacity.IsValid || policy.ImageDigest is not { Length: 71 } || !policy.ImageDigest.StartsWith("sha256:", StringComparison.Ordinal) ||
                policy.ImageDigest.AsSpan(7).ContainsAnyExcept("0123456789abcdef") || !policy.Features.Contains("docker") ||
                policy.Features.Length > 64 || policy.Features.Any(x => !ComputeSpecification.Identifier(x)))
                throw new InvalidDataException("Invalid installed Office compute policy.");
            var enrollment = new ComputeProviderEnrollment(policy.OrganizationId, pin.OfficeId, Provider,
                new(pin.AssignmentSigningKeyId, Convert.ToBase64String(pin.AssignmentVerificationPublicKey)));
            var directory = Path.Combine(storage.ResolveStateDirectory(), "compute-journal");
            Directory.CreateDirectory(directory);
            var journal = OfficeComputeJournal.Open(directory, enrollment, capacity, clock);
            await journal.InitializeAsync(token);
            var template = new ComputeTemplate("office-linux-" + policy.ImageDigest.Substring(7, 48), "linux", "x64",
                policy.ImageDigest, policy.Features.ToHashSet(StringComparer.Ordinal));
            var verifier = new ComputeDispatchVerifier(enrollment, new(Provider, [template.Id], Actions, [ComputeNetworkMode.None], false, false),
                capacity.Resources, new Dictionary<string, ComputeTemplate> { [template.Id] = template }, clock);
            var backend = new HyperVIsolationBackend(new() { HelperExecutablePath = runtime.HelperExecutablePath,
                HelperExecutableDigest = runtime.HelperExecutableDigest, GuestImagePath = policy.ImagePath,
                GuestImageDigest = policy.ImageDigest, BrokerProtocolVersion = "1.0" }, clock);
            return state = new(pin, template, backend, journal, verifier);
        }
        finally { initialization.Release(); }
    }

    private readonly SemaphoreSlim verification = new(1);
    private readonly HyperVIsolationBackend installedBackend = new(runtime, clock);
    private DateTimeOffset imageSignerExpiresAt;
    private DateTimeOffset providerVerifiedUntil;
    private async Task<bool> CanProvisionAsync(CancellationToken token)
    {
        if (!policy.Enabled) return false;
        await verification.WaitAsync(token);
        try
        {
            var now = clock.GetUtcNow();
            if (imageSignerExpiresAt <= now)
            {
                foreach (var path in new[] { policy.ImagePath, policy.SignaturePath, runtime.GuestImageSigningCertificatePath,
                             runtime.HelperExecutablePath })
                    WindowsComputeProtectedPaths.Verify(path);
                if (!await ExternalPlatformIsolationBackend.VerifyFileDigestAsync(policy.ImagePath, policy.ImageDigest, token) ||
                    !await ExternalPlatformIsolationBackend.VerifyGuestImageSignatureAsync(policy.ImagePath, policy.SignaturePath,
                        runtime.GuestImageSigningCertificatePath, runtime.GuestImageSigningCertificateThumbprint, token)) return false;
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(runtime.GuestImageSigningCertificatePath);
                imageSignerExpiresAt = certificate.NotAfter.ToUniversalTime();
            }
            if (providerVerifiedUntil <= now)
            {
                var probe = await installedBackend.ProbeAsync(token);
                if (!probe.IsAvailable || probe.Certification is not { } certification || !certification.IsActiveAt(clock.GetUtcNow()))
                    return false;
                providerVerifiedUntil = now.AddMinutes(5);
                if (certification.ExpiresAt is { } expires && expires < providerVerifiedUntil) providerVerifiedUntil = expires;
            }
            return clock.GetUtcNow() < providerVerifiedUntil && clock.GetUtcNow() < imageSignerExpiresAt;
        }
        finally { verification.Release(); }
    }

    public async Task<ComputeExchangeResult> ExchangeAsync(ComputeExchange request, CancellationToken token)
    {
        var response = new ComputeExchangeResult();
        if (await OpenAsync(token) is not { } local) return response;
        switch (request.OperationCase)
        {
            case ComputeExchange.OperationOneofCase.Inventory:
                if (await CanProvisionAsync(token))
                {
                    var offer = new OfficeComputeOffer { OrganizationId = policy.OrganizationId.ToString("D"), TemplateId = local.Template.Id,
                        ImageDigest = local.Template.ImageDigest, MaximumEnvironments = policy.MaximumEnvironments,
                        CpuCount = policy.CpuCount, MemoryMib = policy.MemoryMiB, DiskMib = policy.DiskMiB };
                    offer.Features.Add(local.Template.Features.Order(StringComparer.Ordinal));
                    response.CapacityOffer = offer.ToByteString();
                }
                // Pagination also progresses past expired evidence waiting for a fresh observation.
                Guid? after = request.AfterOperationId.Length == 0 ? null : Guid.ParseExact(request.AfterOperationId, "D");
                var entries = await local.Journal.ListResultsAsync(after, 1, token);
                if (after.HasValue && entries.Count == 0) entries = await local.Journal.ListResultsAsync(1, token);
                foreach (var entry in entries)
                    response.ResultPayloads.Add(ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(entry.Result, ComputeProtocol.Json)));
                if ((await local.Journal.ListMaintenanceEventsAsync(1, token)).FirstOrDefault() is { } maintenance)
                    response.MaintenanceEntry = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(maintenance, ComputeProtocol.Json));
                break;
            case ComputeExchange.OperationOneofCase.SignedPacket when request.SignedPacket.Length is > 0 and <= 1024 * 1024:
                var packet = JsonSerializer.Deserialize<ComputeDispatchPacket>(request.SignedPacket.Span, ComputeProtocol.Json)
                    ?? throw new InvalidDataException("Missing compute dispatch.");
                var verified = local.Verifier.Verify(packet);
                var cleanup = verified.Authorization.Action is InfrastructureActions.Stop or InfrastructureActions.Destroy;
                if (verified.Specification is not { OperatingSystem: "linux", Architecture: "x64", Persistence: ComputePersistence.Ephemeral } spec ||
                    spec.NetworkPolicy.Mode != ComputeNetworkMode.None || spec.NetworkPolicy.AllowOutbound || spec.NetworkPolicy.Ports.Count != 0 ||
                    spec.Resources.GpuCount != 0 || !cleanup && (spec.TemplateId != local.Template.Id || !await CanProvisionAsync(token)))
                    throw new UnauthorizedAccessException("Compute dispatch exceeds the Office installation policy.");
                await local.Journal.RunPhysicalAsync(verified, (decision, known, ct) =>
                    ApplyAsync(local, verified, decision, known, ct), token);
                break;
            case ComputeExchange.OperationOneofCase.SignedReceipt when request.SignedReceipt.Length is > 0 and <= 2048:
            case ComputeExchange.OperationOneofCase.MaintenanceReceipt when request.MaintenanceReceipt.Length is > 0 and <= 2048:
                var isMaintenance = request.OperationCase == ComputeExchange.OperationOneofCase.MaintenanceReceipt;
                var receipt = OfficeComputeReceipt.Parser.ParseFrom(isMaintenance ? request.MaintenanceReceipt : request.SignedReceipt);
                var id = Guid.ParseExact(receipt.OperationId, "D");
                if (isMaintenance && receipt.Sequence != 1) throw new InvalidDataException("Invalid maintenance receipt.");
                var signedBytes = isMaintenance
                    ? OfficeComputeReceiptEncoding.MaintenancePayload(local.Trust.OfficeId, id, receipt.PayloadDigest)
                    : OfficeComputeReceiptEncoding.Payload(local.Trust.OfficeId, id, receipt.Sequence, receipt.PayloadDigest);
                using (var key = ECDsa.Create())
                {
                    key.ImportSubjectPublicKeyInfo(local.Trust.AssignmentVerificationPublicKey, out _);
                    if (!key.VerifyData(signedBytes, receipt.Signature.Span, HashAlgorithmName.SHA256))
                        throw new UnauthorizedAccessException("Invalid compute receipt signature.");
                }
                if (isMaintenance) await local.Journal.AcknowledgeMaintenanceEventAsync(id, receipt.PayloadDigest, token);
                else await local.Journal.AcknowledgeResultAsync(new(id, receipt.Sequence, receipt.PayloadDigest, true), token);
                break;
            default: throw new InvalidDataException("Unknown Office compute request.");
        }
        return response;
    }

    private async Task<ComputePhysicalOutcome<bool>> ApplyAsync(State local, VerifiedComputeDispatch dispatch,
        ComputeJournalDecision decision, string? known, CancellationToken token)
    {
        var claim = dispatch.Authorization;
        var id = claim.EnvironmentId;
        var backend = local.Backend;
        var execute = decision == ComputeJournalDecision.Execute;
        var kind = WorkloadKind.ComputeEnvironment;
        if (claim.Action == InfrastructureActions.Destroy)
        {
            var removed = execute ? await backend.DestroyAttemptAsync(id, id, kind, token)
                : await backend.ObserveDestructionAsync(id, id, kind, token);
            if (!removed.Confirmed || known is not null && known != removed.Handle?.ProviderInstanceId)
                throw new IOException("Compute VM removal is unconfirmed.");
            return new(true, removed.Handle?.ProviderInstanceId, new(ComputeLifecycleState.Destroyed, TeardownConfirmed: true));
        }
        IsolationWorkloadHandle? handle;
        if (execute && claim.Action == InfrastructureActions.Provision)
        {
            var spec = dispatch.Specification;
            var limits = new WorkloadResourceLimits(spec.Resources.CpuCount, 100 * spec.Resources.CpuCount,
                checked((int)spec.Resources.MemoryMiB), checked((int)spec.Resources.DiskMiB), 4096, 65536, TimeSpan.FromDays(7));
            var image = new GuestImageReference(local.Template.Id, "1", local.Template.ImageDigest, "linux", "x64");
            var workload = new ComputeEnvironmentWorkloadSpecification(id, image, limits,
                new(id, "1.0", "", image.Digest, null, claim.EnvironmentLeaseExpiresAt), claim.OrganizationId, claim.InstallationId);
            handle = await backend.CreateAttemptAsync(workload, id, token);
            token.ThrowIfCancellationRequested();
            await backend.StartAsync(handle, token);
        }
        else
        {
            handle = await backend.ObserveCreationAsync(id, id, kind, token);
            if (handle is null || known is not null && known != handle.ProviderInstanceId)
                throw new IOException("Cannot confirm the exact compute VM.");
            if (execute && claim.Action is InfrastructureActions.Stop or InfrastructureActions.Restart)
                await backend.StopAsync(handle, TimeSpan.Zero, token);
            if (execute && claim.Action is InfrastructureActions.Start or InfrastructureActions.Restart)
                await backend.StartAsync(handle, token);
        }
        var status = await backend.InspectAsync(handle, token);
        var running = status?.State == IsolationWorkloadState.Running;
        var vm = Guid.ParseExact(handle.ProviderInstanceId, "N");
        if (claim.Action == InfrastructureActions.Execute)
        {
            ComputeWorkloadResult output;
            if (!execute) output = new(ErrorCode: "outcome-unknown");
            else if (!running) output = new(ErrorCode: "guest-unavailable");
            else if (dispatch.Workload is { Command: { } command, NodeDependencies: null })
                output = new(Command: await RunCommandAsync(command, token));
            else if (dispatch.Workload?.BrowserTest is { } test)
                output = new(Command: await ComputeBrowserTestExecutor.ExecuteAsync(test, RunCommandAsync, token));
            else output = new(ErrorCode: "workload-unsupported");
            return new(true, handle.ProviderInstanceId, new(running ? ComputeLifecycleState.Ready : ComputeLifecycleState.Failed, Workload: output));

            // The guest reports truncation. Leave space for base64 output and the signed result envelope.
            Task<ComputeGuestWireResult> RunCommandAsync(ComputeGuestCommand command, CancellationToken ct) =>
                ComputeGuestCommandClient.ExecuteAsync(inner => socket.ConnectAsync(vm, inner),
                    command with { MaximumOutputBytes = Math.Min(command.MaximumOutputBytes, 16 * 1024) }, "linux", ct);
        }
        if (claim.Action == InfrastructureActions.Stop && status?.State == IsolationWorkloadState.Stopped)
            return new(true, handle.ProviderInstanceId, new(ComputeLifecycleState.Stopped));
        var ready = running && await ComputeGuestReadiness.ProbeAsync(ct => socket.ConnectAsync(vm, ct), "linux", "x64", token);
        return new(true, handle.ProviderInstanceId, new(ready ? ComputeLifecycleState.Ready :
            running ? ComputeLifecycleState.Bootstrapping : ComputeLifecycleState.Failed));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await OpenAsync(stoppingToken) is { } local)
                {
                    var monitor = new ComputeLeaseMonitor(local.Journal, (id, ct) => local.Journal.EnforceExpiredLeaseAsync(id,
                        async (lease, inner) =>
                        {
                            var removed = await local.Backend.DestroyAttemptAsync(id, id, WorkloadKind.ComputeEnvironment, inner);
                            return new(removed.Confirmed, lease.ResourceId);
                        }, ct, async (lease, inner) =>
                        {
                            var observed = await local.Backend.ObserveDestructionAsync(id, id, WorkloadKind.ComputeEnvironment, inner);
                            return observed.Confirmed && (lease.ResourceId is null || lease.ResourceId == observed.Handle?.ProviderInstanceId);
                        }), clock, _ => { });
                    await monitor.RunAsync(stoppingToken);
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            { logger.LogError(e, "Office compute lease recovery is unavailable."); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
