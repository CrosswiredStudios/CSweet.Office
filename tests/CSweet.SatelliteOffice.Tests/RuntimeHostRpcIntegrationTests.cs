using CSweet.SatelliteOffice.Runtime.Abstractions;
using CSweet.SatelliteOffice.Runtime.Core;
using CSweet.SatelliteOffice.Runtime.LocalRpc;
using CSweet.SatelliteOffice.Runtime.Protocol;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.SatelliteOffice.Contracts.Security;

namespace CSweet.SatelliteOffice.Tests;

public sealed class RuntimeHostRpcIntegrationTests
{
    [Fact]
    public async Task ProbeFailsClosedWhenProviderHasNoGuestChannelConnector()
    {
        var descriptor = Descriptor();
        var dispatcher = new RuntimeHostRequestDispatcher(
            [new BackendAdapter(new InMemoryAgentIsolationProvider(descriptor))], [], NewGate());
        var request = new RuntimeHostEnvelope
        {
            ProtocolVersion = "1.0",
            RequestId = Guid.NewGuid().ToString("D"),
            ProbeRequest = new ProbeRequest { ProviderId = descriptor.ProviderId }
        };
        var responses = new List<RuntimeHostEnvelope>();

        await foreach (var response in dispatcher.DispatchAsync(request)) responses.Add(response);

        var probe = Assert.Single(responses).ProbeResponse;
        Assert.False(probe.Available);
        Assert.Contains("guest-channel connector", probe.UnavailableReason, StringComparison.Ordinal);
        Assert.Empty(probe.CertificationJson);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsPipeSecurity_GrantsExactDuplexClientConnectionRights()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var factory = typeof(RuntimeHostRpcServer).GetMethod(
            "CreateWindowsPipeSecurity",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var security = (PipeSecurity)factory.Invoke(null, [sid.Value])!;
        var clientRule = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Single(rule => rule.IdentityReference.Equals(sid) &&
                            rule.PipeAccessRights == (PipeAccessRights.ReadWrite |
                                                      PipeAccessRights.Synchronize |
                                                      PipeAccessRights.CreateNewInstance));

        Assert.Equal(AccessControlType.Allow, clientRule.AccessControlType);
    }

    [Fact]
    public async Task ClientAndServer_AuthenticateAndDispatchTypedLifecycle()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var officeId = Guid.NewGuid();
        var trust = Trust(signingKey, officeId);
        var gate = NewGate();
        gate.Pin(trust);
        var descriptor = Descriptor();
        var backend = new InMemoryAgentIsolationProvider(descriptor);
        var endpoint = new RuntimeHostEndpointOptions
        {
            NamedPipeName = $"csweet-runtime-test-{Guid.NewGuid():N}",
            UnixSocketPath = Path.Combine(Path.GetTempPath(), $"csweet-runtime-test-{Guid.NewGuid():N}.sock"),
            ConnectTimeoutSeconds = 5
        };
        if (OperatingSystem.IsWindows())
            endpoint.AllowedClientSid = WindowsIdentity.GetCurrent().User?.Value;
        var authentication = new RuntimeHostAuthenticationOptions
        {
            KeyId = "test",
            SharedKeyBase64 = Convert.ToBase64String(new byte[32])
        };
        var serverAuthenticator = new RuntimeHostRequestAuthenticator(authentication, TimeProvider.System);
        var clientAuthenticator = new RuntimeHostRequestAuthenticator(authentication, TimeProvider.System);
        var server = new RuntimeHostRpcServer(
            endpoint,
            serverAuthenticator,
            new RuntimeHostRequestDispatcher([new BackendAdapter(backend)], [new TestGuestConnector(descriptor.ProviderId)], gate),
            [new TestGuestConnector(descriptor.ProviderId)],
            gate);
        using var stop = new CancellationTokenSource();
        var serverTask = server.RunAsync(stop.Token);
        await Task.Delay(100);
        var client = new RuntimeHostProviderClient(descriptor, endpoint, clientAuthenticator);
        var workload = Runtime();

        await client.PinHeadquartersTrustAsync(trust);
        var handle = await client.CreateAuthorizedAsync(workload,
            Authorization(signingKey, trust, descriptor.ProviderId, workload));
        await client.StartAsync(handle);
        Assert.Equal(IsolationWorkloadState.Running, (await client.InspectAsync(handle))!.State);
        await client.StopAsync(handle, TimeSpan.Zero);
        await client.DestroyAsync(handle);
        Assert.Null(await client.InspectAsync(handle));

        stop.Cancel();
        try { await serverTask; }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task BackendFailureReturnsCorrelatedTypedErrorInsteadOfClosingPipe()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust(signingKey, Guid.NewGuid());
        var gate = NewGate();
        gate.Pin(trust);
        var descriptor = Descriptor();
        var endpoint = new RuntimeHostEndpointOptions
        {
            NamedPipeName = $"csweet-runtime-test-{Guid.NewGuid():N}",
            UnixSocketPath = Path.Combine(Path.GetTempPath(), $"csweet-runtime-test-{Guid.NewGuid():N}.sock"),
            ConnectTimeoutSeconds = 5
        };
        if (OperatingSystem.IsWindows())
            endpoint.AllowedClientSid = WindowsIdentity.GetCurrent().User?.Value;
        var authentication = new RuntimeHostAuthenticationOptions
        {
            KeyId = "test",
            SharedKeyBase64 = Convert.ToBase64String(new byte[32])
        };
        var server = new RuntimeHostRpcServer(
            endpoint,
            new RuntimeHostRequestAuthenticator(authentication, TimeProvider.System),
            new RuntimeHostRequestDispatcher([new FailingBackend(descriptor)], [new TestGuestConnector(descriptor.ProviderId)], gate),
            [new TestGuestConnector(descriptor.ProviderId)],
            gate);
        using var stop = new CancellationTokenSource();
        var serverTask = server.RunAsync(stop.Token);
        await Task.Delay(100);
        var client = new RuntimeHostProviderClient(
            descriptor,
            endpoint,
            new RuntimeHostRequestAuthenticator(authentication, TimeProvider.System));

        var workload = Runtime();
        var exception = await Assert.ThrowsAsync<IsolationUnavailableException>(() =>
            client.CreateAuthorizedAsync(workload, Authorization(signingKey, trust, descriptor.ProviderId, workload)));

        Assert.Contains("provider-create-failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Diagnostic request:", exception.Message, StringComparison.Ordinal);
        stop.Cancel();
        try { await serverTask; }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task IsolationFailurePreservesSanitizedProviderDiagnostic()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust(signingKey, Guid.NewGuid());
        var gate = NewGate();
        gate.Pin(trust);
        var descriptor = Descriptor();
        var dispatcher = new RuntimeHostRequestDispatcher(
            [new UnavailableBackend(descriptor)],
            [new TestGuestConnector(descriptor.ProviderId)], gate);
        var workload = Runtime();
        var request = new RuntimeHostEnvelope
        {
            ProtocolVersion = "1.0",
            RequestId = Guid.NewGuid().ToString("D"),
            CreateRequest = RuntimeHostProtocolMapper.ToProtocol(descriptor.ProviderId, workload)
        };
        request.CreateRequest.Authorization = ToProtocol(
            Authorization(signingKey, trust, descriptor.ProviderId, workload));
        var responses = new List<RuntimeHostEnvelope>();

        await foreach (var response in dispatcher.DispatchAsync(request)) responses.Add(response);

        var result = Assert.Single(responses).CreateResponse;
        Assert.False(result.Success);
        Assert.Equal("provider-unavailable", result.ErrorCode);
        Assert.Equal(
            "Platform helper rejected the operation (hyperv-command-failed): New-VM permission denied.",
            result.SanitizedError);
    }

    [Fact]
    public async Task DispatcherSurfacesSignedAuthorizationRejectionWithCorrelatedDiagnostic()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust(key, Guid.NewGuid());
        var gate = NewGate();
        gate.Pin(trust);
        var descriptor = Descriptor();
        var dispatcher = new RuntimeHostRequestDispatcher(
            [new BackendAdapter(new InMemoryAgentIsolationProvider(descriptor))],
            [new TestGuestConnector(descriptor.ProviderId)], gate);
        var workload = Runtime();
        var request = new RuntimeHostEnvelope
        {
            ProtocolVersion = "1.0",
            RequestId = Guid.NewGuid().ToString("D"),
            CreateRequest = RuntimeHostProtocolMapper.ToProtocol(descriptor.ProviderId, workload)
        };
        var authorization = Authorization(key, trust, descriptor.ProviderId, workload);
        request.CreateRequest.Authorization = ToProtocol(authorization with
        {
            SpecificationJson = authorization.SpecificationJson + " "
        });
        var responses = new List<RuntimeHostEnvelope>();

        await foreach (var response in dispatcher.DispatchAsync(request)) responses.Add(response);

        var result = Assert.Single(responses).CreateResponse;
        Assert.False(result.Success);
        Assert.Equal("authorization-rejected", result.ErrorCode);
        Assert.Contains("Diagnostic request:", result.SanitizedError, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivilegedAuthorizationRejectsReplayAndProviderSubstitution()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust(key, Guid.NewGuid());
        var gate = NewGate();
        gate.Pin(trust);
        var workload = Runtime();
        var authorization = Authorization(key, trust, "memory-test", workload);
        var request = RuntimeHostProtocolMapper.ToProtocol("memory-test", workload);
        request.Authorization = ToProtocol(authorization);

        Assert.Equal(workload.WorkloadId, gate.ValidateAndCommit(request).WorkloadId);
        Assert.Throws<InvalidDataException>(() => gate.ValidateAndCommit(request));

        var substituted = RuntimeHostProtocolMapper.ToProtocol("other-provider", workload);
        substituted.Authorization = ToProtocol(Authorization(key, trust, "memory-test", Runtime()));
        Assert.Throws<InvalidDataException>(() => gate.ValidateAndCommit(substituted));

        var mismatchedWorkload = RuntimeHostProtocolMapper.ToProtocol("memory-test", workload);
        mismatchedWorkload.Authorization = ToProtocol(authorization with { WorkloadId = Guid.NewGuid() });
        var mismatch = Assert.Throws<InvalidDataException>(() => gate.ValidateAndCommit(mismatchedWorkload));
        Assert.Contains("identifier does not match", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivilegedAuthorizationRejectsTamperingExpiryAndTrustReplacement()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust(key, Guid.NewGuid());
        var gate = NewGate();
        gate.Pin(trust);
        var workload = Runtime();
        var valid = Authorization(key, trust, "memory-test", workload);

        var tampered = RuntimeHostProtocolMapper.ToProtocol("memory-test", workload);
        tampered.Authorization = ToProtocol(valid with { SpecificationJson = valid.SpecificationJson + " " });
        Assert.Throws<InvalidDataException>(() => gate.ValidateAndCommit(tampered));

        var expired = RuntimeHostProtocolMapper.ToProtocol("memory-test", workload);
        expired.Authorization = ToProtocol(valid with
        {
            AssignmentId = Guid.NewGuid(),
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-6),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        Assert.Throws<InvalidDataException>(() => gate.ValidateAndCommit(expired));

        using var replacement = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => gate.Pin(Trust(replacement, trust.SatelliteOfficeId)));
    }

    [Fact]
    public void PrivilegedLifecycleRejectsForgedAndExpiredProviderHandles()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust(key, Guid.NewGuid());
        var gate = NewGate();
        gate.Pin(trust);
        var workload = Runtime();
        var request = RuntimeHostProtocolMapper.ToProtocol("memory-test", workload);
        request.Authorization = ToProtocol(Authorization(key, trust, "memory-test", workload));
        gate.ValidateAndCommit(request);
        var handle = new IsolationWorkloadHandle(
            "memory-test", workload.WorkloadId, "provider-instance", workload.Kind);

        gate.RegisterHandle(request, handle);

        Assert.True(gate.IsHandleAuthorized(handle));
        Assert.False(gate.IsHandleAuthorized(handle with { ProviderInstanceId = "forged" }));
        gate.RemoveHandle(handle);
        Assert.False(gate.IsHandleAuthorized(handle, allowTermination: true));
    }

    private static IsolationProviderDescriptor Descriptor() => new(
        "memory-test",
        "Memory test provider",
        "1.0",
        "test",
        "test",
        0,
        new IsolationProviderCapabilities(
            IsolationAssurance.None,
            false,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            false,
            false));

    private static RuntimeWorkloadSpecification Runtime()
    {
        var guest = "sha256:" + new string('a', 64);
        var artifact = "sha256:" + new string('b', 64);
        return new RuntimeWorkloadSpecification(
            Guid.NewGuid(),
            new GuestImageReference("runtime", "1.0", guest, "linux", "x64"),
            new WorkloadResourceLimits(1, 100, 512, 512, 100, 1024, TimeSpan.FromMinutes(1)),
            new BrokerChannelLease(Guid.NewGuid(), "1.0", "a-sufficiently-long-boot-token", guest, artifact, DateTimeOffset.UtcNow.AddMinutes(1)),
            new AgentArtifactReference(artifact, "signature", "1.0", "linux", "x64"),
            new RuntimeAgentIdentity(Guid.NewGuid(), Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            ["/app/agent"]);
    }

    private static RuntimeHostAuthorizationGate NewGate() => new(
        new RuntimeHostAuthorizationOptions
        {
            StateDirectory = Path.Combine(Path.GetTempPath(), $"csweet-authorization-{Guid.NewGuid():N}")
        }, TimeProvider.System);

    private static PinnedHeadquartersTrust Trust(ECDsa key, Guid officeId) =>
        new(officeId, "test-assignment-key", key.ExportSubjectPublicKeyInfo());

    private static CSweet.SatelliteOffice.Runtime.Abstractions.SignedWorkloadAuthorization Authorization(
        ECDsa key,
        PinnedHeadquartersTrust trust,
        string providerId,
        WorkloadSpecification workload)
    {
        var json = JsonSerializer.Serialize(workload, workload.GetType());
        var digest = AssignmentEnvelope.Digest(json);
        var assignmentId = Guid.NewGuid();
        var issued = DateTimeOffset.UtcNow.AddSeconds(-1);
        var expires = issued.AddMinutes(5);
        return new CSweet.SatelliteOffice.Runtime.Abstractions.SignedWorkloadAuthorization(
            AssignmentEnvelope.CurrentAuthorizationVersion,
            trust.SatelliteOfficeId,
            assignmentId,
            workload.WorkloadId,
            1,
            providerId,
            json,
            digest,
            trust.AssignmentSigningKeyId,
            key.SignData(AssignmentEnvelope.Payload(
                trust.SatelliteOfficeId, assignmentId, workload.WorkloadId, 1,
                providerId, digest, issued, expires), HashAlgorithmName.SHA256),
            issued,
            expires);
    }

    private static CSweet.SatelliteOffice.Runtime.Protocol.SignedWorkloadAuthorization ToProtocol(
        CSweet.SatelliteOffice.Runtime.Abstractions.SignedWorkloadAuthorization authorization) => new()
    {
        AuthorizationVersion = authorization.AuthorizationVersion,
        SatelliteOfficeId = authorization.SatelliteOfficeId.ToString("D"),
        AssignmentId = authorization.AssignmentId.ToString("D"),
        WorkloadId = authorization.WorkloadId.ToString("D"),
        FencingEpoch = authorization.FencingEpoch,
        ProviderId = authorization.ProviderId,
        SpecificationJson = authorization.SpecificationJson,
        SpecificationSha256 = authorization.SpecificationSha256,
        SignatureKeyId = authorization.SignatureKeyId,
        Signature = Google.Protobuf.ByteString.CopyFrom(authorization.Signature),
        IssuedAtUnixSeconds = authorization.IssuedAt.ToUnixTimeSeconds(),
        ExpiresAtUnixSeconds = authorization.ExpiresAt.ToUnixTimeSeconds()
    };

    private sealed class BackendAdapter(InMemoryAgentIsolationProvider inner) : IPlatformIsolationBackend
    {
        public IsolationProviderDescriptor Descriptor => inner.Descriptor;
        public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => inner.ProbeAsync(cancellationToken);
        public Task<IsolationWorkloadHandle> CreateAsync(WorkloadSpecification workload, CancellationToken cancellationToken = default) => inner.CreateAsync(workload, cancellationToken);
        public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => inner.StartAsync(handle, cancellationToken);
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => inner.InspectAsync(handle, cancellationToken);
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => inner.StopAsync(handle, gracePeriod, cancellationToken);
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => inner.DestroyAsync(handle, cancellationToken);
        public IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, CancellationToken cancellationToken = default) => inner.StreamLogsAsync(handle, maximumBytes, cancellationToken);
    }

    private sealed class TestGuestConnector(string providerId) : IPlatformGuestChannelConnector
    {
        public string ProviderId { get; } = providerId;
        public Task<Stream> OpenGuestChannelAsync(
            IsolationWorkloadHandle handle,
            CancellationToken cancellationToken = default) => Task.FromResult<Stream>(Stream.Null);
    }

    private sealed class FailingBackend(IsolationProviderDescriptor descriptor) : IPlatformIsolationBackend
    {
        public IsolationProviderDescriptor Descriptor { get; } = descriptor;
        public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IsolationProviderProbeResult(Descriptor, true, null, null));
        public Task<IsolationWorkloadHandle> CreateAsync(
            WorkloadSpecification workload,
            CancellationToken cancellationToken = default) =>
            throw new IOException("The test backend could not create a VM.");
        public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(
            IsolationWorkloadHandle handle,
            int maximumBytes,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class UnavailableBackend(IsolationProviderDescriptor descriptor) : IPlatformIsolationBackend
    {
        public IsolationProviderDescriptor Descriptor { get; } = descriptor;
        public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IsolationProviderProbeResult(Descriptor, true, null, null));
        public Task<IsolationWorkloadHandle> CreateAsync(
            WorkloadSpecification workload,
            CancellationToken cancellationToken = default) =>
            throw new IsolationUnavailableException(
                "Platform helper rejected the operation (hyperv-command-failed): New-VM permission denied.");
        public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(
            IsolationWorkloadHandle handle,
            int maximumBytes,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
