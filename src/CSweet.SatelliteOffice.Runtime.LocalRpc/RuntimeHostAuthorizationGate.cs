using System.Security.Cryptography;
using System.Text.Json;
using A = CSweet.SatelliteOffice.Runtime.Abstractions;
using P = CSweet.SatelliteOffice.Runtime.Protocol;
using W = CSweet.SatelliteOffice.Contracts.Workloads;
using CSweet.SatelliteOffice.Contracts.Security;

namespace CSweet.SatelliteOffice.Runtime.LocalRpc;

public sealed class RuntimeHostAuthorizationOptions
{
    public const string SectionName = "CSweet:SatelliteOffice:RuntimeHost:Authorization";
    public string StateDirectory { get; set; } = string.Empty;
    public int MaximumAuthorizationLifetimeSeconds { get; set; } = 600;
    public int MaximumClockSkewSeconds { get; set; } = 120;

    public string ResolveStateDirectory()
    {
        if (!string.IsNullOrWhiteSpace(StateDirectory)) return Path.GetFullPath(StateDirectory);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(common) ? AppContext.BaseDirectory : common,
            "CSweet", "SatelliteOffice", "authorization");
    }
}

/// <summary>
/// The privileged authorization boundary. The network-facing node cannot alter pinned
/// Headquarters trust or turn a signed assignment into a different platform operation.
/// </summary>
public sealed class RuntimeHostAuthorizationGate
{
    private readonly RuntimeHostAuthorizationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private string TrustPath => Path.Combine(_options.ResolveStateDirectory(), "headquarters-trust.json");
    private string ReplayPath => Path.Combine(_options.ResolveStateDirectory(), "accepted-assignments.json");
    private string HandlesPath => Path.Combine(_options.ResolveStateDirectory(), "authorized-workload-handles.json");

    public RuntimeHostAuthorizationGate(RuntimeHostAuthorizationOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        if (options.MaximumAuthorizationLifetimeSeconds is < 30 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumAuthorizationLifetimeSeconds));
        if (options.MaximumClockSkewSeconds is < 0 or > 600)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumClockSkewSeconds));
    }

    public void Pin(A.PinnedHeadquartersTrust requested)
    {
        ValidateTrust(requested);
        lock (_sync)
        {
            Directory.CreateDirectory(_options.ResolveStateDirectory());
            var existing = ReadTrust();
            if (existing is not null)
            {
                if (!string.Equals(existing.AssignmentSigningKeyId, requested.AssignmentSigningKeyId, StringComparison.Ordinal) ||
                    !CryptographicOperations.FixedTimeEquals(existing.AssignmentVerificationPublicKey, requested.AssignmentVerificationPublicKey))
                    throw new InvalidDataException("The pinned Headquarters assignment trust does not match this enrollment.");
                if (existing.SatelliteOfficeId == Guid.Empty && requested.SatelliteOfficeId != Guid.Empty)
                {
                    WriteAtomic(TrustPath, JsonSerializer.SerializeToUtf8Bytes(requested));
                    return;
                }
                if (existing.SatelliteOfficeId != requested.SatelliteOfficeId)
                    throw new InvalidDataException("The pinned Headquarters assignment trust belongs to another Satellite Office.");
                return;
            }
            WriteAtomic(TrustPath, JsonSerializer.SerializeToUtf8Bytes(requested));
        }
    }

    public W.WorkloadSpecification ValidateAndCommit(P.CreateWorkloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authorization = request.Authorization ??
            throw new InvalidDataException("A signed workload authorization is required.");
        var trust = ReadTrust() ??
            throw new InvalidDataException("Headquarters assignment trust has not been pinned.");
        if (authorization.AuthorizationVersion != AssignmentEnvelope.CurrentAuthorizationVersion)
            throw new InvalidDataException("The workload authorization version is unsupported.");
        if (!Guid.TryParse(authorization.SatelliteOfficeId, out var officeId) || officeId != trust.SatelliteOfficeId ||
            !Guid.TryParse(authorization.AssignmentId, out var assignmentId) || assignmentId == Guid.Empty ||
            !Guid.TryParse(authorization.WorkloadId, out var workloadId) || workloadId == Guid.Empty)
            throw new InvalidDataException("The workload authorization identifiers are invalid.");
        if (!string.Equals(authorization.SignatureKeyId, trust.AssignmentSigningKeyId, StringComparison.Ordinal))
            throw new InvalidDataException("The workload authorization signing key does not match pinned Headquarters trust.");
        if (!string.Equals(authorization.ProviderId, request.ProviderId, StringComparison.Ordinal))
            throw new InvalidDataException("The workload authorization provider does not match the platform request.");
        if (!string.Equals(authorization.WorkloadId, request.WorkloadId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The workload authorization identifier does not match the signed workload specification request.");

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(authorization.IssuedAtUnixSeconds);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(authorization.ExpiresAtUnixSeconds);
        var now = _timeProvider.GetUtcNow();
        if (issuedAt > now.AddSeconds(_options.MaximumClockSkewSeconds) || expiresAt <= now ||
            expiresAt - issuedAt > TimeSpan.FromSeconds(_options.MaximumAuthorizationLifetimeSeconds))
            throw new InvalidDataException("The workload authorization is expired or outside its permitted lifetime.");
        var digest = AssignmentEnvelope.Digest(authorization.SpecificationJson);
        if (!string.Equals(digest, authorization.SpecificationSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The signed workload specification digest is invalid.");

        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(trust.AssignmentVerificationPublicKey, out var read);
            if (read != trust.AssignmentVerificationPublicKey.Length ||
                !key.VerifyData(AssignmentEnvelope.Payload(
                        officeId, assignmentId, workloadId, authorization.FencingEpoch,
                        authorization.ProviderId, authorization.SpecificationSha256, issuedAt, expiresAt),
                    authorization.Signature.Span, HashAlgorithmName.SHA256))
                throw new InvalidDataException("The workload authorization signature is invalid.");
        }

        var workload = RuntimeHostProtocolMapper.DeserializeSpecification(authorization.SpecificationJson);
        if (workload.WorkloadId != workloadId)
            throw new InvalidDataException("The signed specification has a different workload identifier.");

        lock (_sync)
        {
            var accepted = ReadReplayState();
            if (accepted.TryGetValue(assignmentId, out var previousEpoch) && previousEpoch >= authorization.FencingEpoch)
                throw new InvalidDataException("The workload authorization was already accepted or fenced by a newer epoch.");
            accepted[assignmentId] = authorization.FencingEpoch;
            Directory.CreateDirectory(_options.ResolveStateDirectory());
            WriteAtomic(ReplayPath, JsonSerializer.SerializeToUtf8Bytes(accepted));
        }
        return workload;
    }

    public void RegisterHandle(P.CreateWorkloadRequest request, A.IsolationWorkloadHandle handle)
    {
        var authorization = request.Authorization ??
            throw new InvalidDataException("A signed workload authorization is required.");
        if (!Guid.TryParse(authorization.AssignmentId, out var assignmentId) ||
            !Guid.TryParse(authorization.WorkloadId, out var workloadId) ||
            workloadId != handle.WorkloadId ||
            !string.Equals(authorization.ProviderId, handle.ProviderId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(handle.ProviderInstanceId))
            throw new InvalidDataException("The provider returned a handle outside the authorized workload scope.");
        var workload = RuntimeHostProtocolMapper.DeserializeSpecification(authorization.SpecificationJson);
        var expiresAt = workload.BrokerLease.ExpiresAt;
        if (expiresAt <= _timeProvider.GetUtcNow())
            throw new InvalidDataException("The provider returned a handle for an expired workload authorization.");

        lock (_sync)
        {
            var handles = ReadHandles();
            handles[workloadId] = new AuthorizedHandle(
                assignmentId, authorization.FencingEpoch, handle.ProviderId, workloadId,
                handle.ProviderInstanceId, (int)handle.Kind, expiresAt);
            Directory.CreateDirectory(_options.ResolveStateDirectory());
            WriteAtomic(HandlesPath, JsonSerializer.SerializeToUtf8Bytes(handles));
        }
    }

    public bool IsHandleAuthorized(A.IsolationWorkloadHandle handle, bool allowTermination = false)
    {
        lock (_sync)
        {
            var handles = ReadHandles();
            if (!handles.TryGetValue(handle.WorkloadId, out var accepted) ||
                !string.Equals(accepted.ProviderId, handle.ProviderId, StringComparison.Ordinal) ||
                !string.Equals(accepted.ProviderInstanceId, handle.ProviderInstanceId, StringComparison.Ordinal) ||
                accepted.WorkloadKind != (int)handle.Kind)
                return false;
            return allowTermination || accepted.ExpiresAt > _timeProvider.GetUtcNow();
        }
    }

    public void RemoveHandle(A.IsolationWorkloadHandle handle)
    {
        lock (_sync)
        {
            var handles = ReadHandles();
            if (!handles.TryGetValue(handle.WorkloadId, out var accepted) ||
                !string.Equals(accepted.ProviderId, handle.ProviderId, StringComparison.Ordinal) ||
                !string.Equals(accepted.ProviderInstanceId, handle.ProviderInstanceId, StringComparison.Ordinal))
                return;
            handles.Remove(handle.WorkloadId);
            Directory.CreateDirectory(_options.ResolveStateDirectory());
            WriteAtomic(HandlesPath, JsonSerializer.SerializeToUtf8Bytes(handles));
        }
    }

    private A.PinnedHeadquartersTrust? ReadTrust()
    {
        if (!File.Exists(TrustPath)) return null;
        var trust = JsonSerializer.Deserialize<A.PinnedHeadquartersTrust>(File.ReadAllBytes(TrustPath)) ??
            throw new InvalidDataException("The pinned Headquarters trust record is invalid.");
        ValidateTrust(trust);
        return trust;
    }

    private Dictionary<Guid, long> ReadReplayState()
    {
        if (!File.Exists(ReplayPath)) return [];
        return JsonSerializer.Deserialize<Dictionary<Guid, long>>(File.ReadAllBytes(ReplayPath)) ??
            throw new InvalidDataException("The privileged assignment replay state is invalid.");
    }

    private Dictionary<Guid, AuthorizedHandle> ReadHandles()
    {
        if (!File.Exists(HandlesPath)) return [];
        return JsonSerializer.Deserialize<Dictionary<Guid, AuthorizedHandle>>(File.ReadAllBytes(HandlesPath)) ??
            throw new InvalidDataException("The privileged workload-handle authorization state is invalid.");
    }

    private static void ValidateTrust(A.PinnedHeadquartersTrust trust)
    {
        if (string.IsNullOrWhiteSpace(trust.AssignmentSigningKeyId) ||
            trust.AssignmentSigningKeyId.Length > 200 || trust.AssignmentVerificationPublicKey.Length is < 64 or > 1024)
            throw new InvalidDataException("The Headquarters assignment trust is invalid.");
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(trust.AssignmentVerificationPublicKey, out var read);
        if (read != trust.AssignmentVerificationPublicKey.Length)
            throw new InvalidDataException("The Headquarters assignment key contains trailing data.");
    }

    private static void WriteAtomic(string path, byte[] contents)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
            stream.Write(contents);
        File.Move(temporary, path, true);
    }

    private sealed record AuthorizedHandle(
        Guid AssignmentId,
        long FencingEpoch,
        string ProviderId,
        Guid WorkloadId,
        string ProviderInstanceId,
        int WorkloadKind,
        DateTimeOffset ExpiresAt);
}
