using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Office.Runtime.Abstractions;
using CSweet.Office.Runtime.Protocol;
using CSweet.Office.Contracts.ControlPlane;
using Grpc.Core;
using Grpc.Net.Client;

namespace CSweet.Office.Node;

public sealed class OfficeWorker(
    OfficeOptions options,
    OfficeStateStore stateStore,
    RuntimeHostInventory inventory,
    OfficeArtifactCache artifactCache,
    IEnumerable<IAgentIsolationProvider> isolationProviders,
    IHttpClientFactory httpClientFactory,
    ControlPlaneServerCertificateValidator certificateValidator,
    ILogger<OfficeWorker> logger) : BackgroundService
{
    private readonly IReadOnlyDictionary<string, IAgentIsolationProvider> _providers = isolationProviders
        .ToDictionary(x => x.Descriptor.ProviderId, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeAssignments = [];
    private readonly SemaphoreSlim _workloadSlots = new(Math.Max(1, options.MaximumConcurrentWorkloads));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stateStore.InitializeMaintenanceSession();
        OfficeState? processSession = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var certificate = stateStore.GetOrCreateCertificate();
                try
                {
                    var state = processSession ?? await stateStore.LoadAsync(stoppingToken) ??
                        await EnrollAsync(certificate, stoppingToken);
                    if (processSession is null)
                    {
                        if (state.SessionEpoch == long.MaxValue)
                            throw new InvalidDataException("The office session epoch is exhausted.");
                        var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var nextEpoch = Math.Max(state.SessionEpoch + 1, nowEpoch);
                        state = state with { SessionEpoch = nextEpoch };
                        await stateStore.SaveAsync(state, stoppingToken);
                        processSession = state;
                    }
                    await PinRuntimeHostTrustAsync(state, stoppingToken);
                    var operational = await RefreshOperationalCertificateAsync(state, certificate, stoppingToken);
                    if (!ReferenceEquals(operational, certificate))
                    {
                        certificate.Dispose();
                        certificate = operational;
                    }
                    await RunControlSessionAsync(state, certificate, stoppingToken);
                }
                finally { certificate.Dispose(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "The office control session ended; reconnecting.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<OfficeState> EnrollAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var token = options.EnrollmentToken;
        string? enrollmentTokenPath = null;
        if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(options.EnrollmentTokenFilePath))
        {
            var tokenPath = Path.GetFullPath(options.EnrollmentTokenFilePath);
            try
            {
                token = (await File.ReadAllTextAsync(tokenPath, cancellationToken)).Trim();
                enrollmentTokenPath = tokenPath;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("The protected enrollment-token input could not be read.", exception);
            }
        }
        if (string.IsNullOrWhiteSpace(token) && !Console.IsInputRedirected)
            throw new InvalidOperationException("An enrollment token must be supplied through protected installer configuration or stdin.");
        if (string.IsNullOrWhiteSpace(token))
            token = (await Console.In.ReadLineAsync(cancellationToken))?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("The enrollment token is missing.");
        var providers = await inventory.ProbeAsync(cancellationToken);
        var request = new ClaimOfficeRequest(
            token ?? string.Empty, options.OfficeName, Environment.MachineName, RuntimeHostInventory.Platform(),
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            typeof(OfficeWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", "1.0",
            certificate.Thumbprint, certificate.SerialNumber,
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            OfficeStateStore.CreateCertificateSigningRequestPem(certificate),
            options.AllocatableCpuCount, options.AllocatableMemoryMb, options.AllocatableDiskMb,
            options.MaximumConcurrentWorkloads, providers, options.SecurityPosture());
        var client = httpClientFactory.CreateClient("control-plane");
        using var response = await client.PostAsJsonAsync("api/offices/claim", request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        ClaimOfficeResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<ClaimOfficeResponse>(
                responseBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException exception)
        {
            var details = responseBody.Trim();
            if (details.Length > 512) details = details[..512];
            throw new InvalidOperationException(
                $"The control plane returned HTTP {(int)response.StatusCode} with an invalid enrollment response" +
                (details.Length == 0 ? "." : $": {details}"), exception);
        }
        if (result is null)
            throw new InvalidDataException("The control plane returned an empty enrollment response.");
        if (!response.IsSuccessStatusCode || !result.Succeeded || result.OfficeId is null ||
            string.IsNullOrWhiteSpace(result.EnrollmentReceipt) || string.IsNullOrWhiteSpace(result.AssignmentSigningKeyId) ||
            string.IsNullOrWhiteSpace(result.AssignmentVerificationPublicKeyBase64))
            throw new InvalidOperationException($"Office enrollment failed ({result.ErrorCode ?? "unknown"}): {result.Message}");
        byte[] assignmentPublicKey;
        try
        {
            assignmentPublicKey = Convert.FromBase64String(result.AssignmentVerificationPublicKeyBase64);
            using var parsedKey = ECDsa.Create();
            parsedKey.ImportSubjectPublicKeyInfo(assignmentPublicKey, out var read);
            if (read != assignmentPublicKey.Length) throw new CryptographicException("The assignment key contains trailing data.");
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidDataException("The control plane returned an invalid assignment verification identity.", exception);
        }
        options.EnrollmentToken = string.Empty;
        var state = new OfficeState(result.OfficeId.Value, result.EnrollmentReceipt,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stateStore.GetCertificatePath(),
            result.AssignmentSigningKeyId, Convert.ToBase64String(assignmentPublicKey));
        await stateStore.SaveAsync(state, cancellationToken);
        if (enrollmentTokenPath is not null)
        {
            try { File.Delete(enrollmentTokenPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception,
                    "The consumed enrollment-token file could not be removed from {EnrollmentTokenPath}.",
                    enrollmentTokenPath);
            }
        }
        logger.LogInformation("Office {OfficeId} enrolled and is awaiting administrator approval.", state.OfficeId);
        return state;
    }

    private async Task PinRuntimeHostTrustAsync(OfficeState state, CancellationToken cancellationToken)
    {
        foreach (var client in _providers.Values.OfType<IRuntimeHostClient>())
            await client.PinHeadquartersTrustAsync(new PinnedHeadquartersTrust(
                state.OfficeId,
                state.AssignmentSigningKeyId,
                Convert.FromBase64String(state.AssignmentVerificationPublicKeyBase64)), cancellationToken);
    }

    private async Task<X509Certificate2> RefreshOperationalCertificateAsync(
        OfficeState state,
        X509Certificate2 current,
        CancellationToken cancellationToken)
    {
        var bootstrapCertificate = string.Equals(current.Subject, current.Issuer, StringComparison.OrdinalIgnoreCase);
        if (!bootstrapCertificate && current.NotAfter > DateTime.UtcNow.AddHours(6)) return current;
        using var client = bootstrapCertificate
            ? httpClientFactory.CreateClient("control-plane")
            : CreateMutualTlsClient(current);
        using var response = await client.PostAsJsonAsync(
            $"api/offices/{state.OfficeId:D}/certificate",
            new OfficeCertificateRequest(bootstrapCertificate ? state.EnrollmentReceipt : string.Empty),
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return current;
        response.EnsureSuccessStatusCode();
        var issued = await response.Content.ReadFromJsonAsync<OfficeCertificateResponse>(cancellationToken)
            ?? throw new InvalidDataException("The operational certificate response was empty.");
        if (!issued.Succeeded || string.IsNullOrWhiteSpace(issued.CertificateBase64))
            return current;
        if (string.Equals(Normalize(current.Thumbprint), Normalize(issued.CertificateThumbprint ?? string.Empty),
                StringComparison.Ordinal))
            return current;
        var installed = stateStore.InstallOperationalCertificate(current, issued.CertificateBase64);
        if (!string.Equals(Normalize(installed.Thumbprint), Normalize(issued.CertificateThumbprint ?? string.Empty),
                StringComparison.Ordinal))
        {
            installed.Dispose();
            throw new CryptographicException("The issued operational certificate thumbprint did not match.");
        }
        logger.LogInformation("Installed rotated operational certificate {Thumbprint} expiring at {ExpiresAt}.",
            installed.Thumbprint, installed.NotAfter);
        return installed;
    }

    private HttpClient CreateMutualTlsClient(X509Certificate2 certificate)
    {
        var handler = certificateValidator.CreateHttpClientHandler(certificate);
        return new HttpClient(handler) { BaseAddress = new Uri(options.ControlPlaneUrl) };
    }

    private async Task RunControlSessionAsync(
        OfficeState state,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        var providers = await inventory.ProbeAsync(cancellationToken);
        if (string.Equals(certificate.Subject, certificate.Issuer, StringComparison.OrdinalIgnoreCase))
        {
            var http = httpClientFactory.CreateClient("control-plane");
            using var heartbeat = await http.PostAsJsonAsync($"api/offices/{state.OfficeId:D}/heartbeat",
                new OfficeHeartbeatRequest(state.EnrollmentReceipt, state.SessionEpoch,
                    options.AllocatableCpuCount, options.AllocatableMemoryMb, options.AllocatableDiskMb,
                    options.MaximumConcurrentWorkloads, providers, options.SecurityPosture()), cancellationToken);
            heartbeat.EnsureSuccessStatusCode();
        }

        var handler = certificateValidator.CreateHttpClientHandler(certificate);
        using var channel = GrpcChannel.ForAddress(options.ControlPlaneUrl, new GrpcChannelOptions { HttpHandler = handler });
        var client = new OfficeGateway.OfficeGatewayClient(channel);
        using var call = client.Connect(cancellationToken: cancellationToken);
        using var writerLock = new SemaphoreSlim(1, 1);
        var readTask = ReadControlMessagesAsync(
            call.ResponseStream, call.RequestStream, writerLock, client, state, cancellationToken);
        try
        {
            var sendInventory = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                var heartbeat = new OfficeHeartbeat
                {
                    OccurredAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    AllocatableCpuCount = options.AllocatableCpuCount,
                    AllocatableMemoryMb = options.AllocatableMemoryMb,
                    AllocatableDiskMb = options.AllocatableDiskMb,
                    MaximumConcurrentWorkloads = options.MaximumConcurrentWorkloads
                };
                heartbeat.SecurityPosture = ProviderInventory(options.SecurityPosture());
                if (sendInventory)
                {
                    heartbeat.Providers.AddRange(providers.Select(ProviderInventory));
                    sendInventory = false;
                }
                await SendAsync(call.RequestStream, writerLock, new OfficeControlMessage
                {
                    ProtocolVersion = "1.0",
                    OfficeId = state.OfficeId.ToString("D"),
                    SessionEpoch = state.SessionEpoch,
                    Heartbeat = heartbeat
                }, cancellationToken);
                await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
                if (readTask.IsCompleted) await readTask;
            }
        }
        finally
        {
            foreach (var active in _activeAssignments.Values) active.Cancel();
            await call.RequestStream.CompleteAsync();
        }
    }

    private static OfficeProviderInventory ProviderInventory(RegisterOfficeProviderRequest provider) => new()
    {
        ProviderId = provider.ProviderId,
        ProviderVersion = provider.ProviderVersion,
        BrokerProtocolVersion = provider.BrokerProtocolVersion,
        GuestImageDigest = provider.GuestImageDigest,
        CertificationSuiteVersion = provider.CertificationSuiteVersion,
        CertificationEvidenceDigest = provider.CertificationEvidenceDigest,
        CertifiedAtUnixSeconds = provider.CertifiedAt.ToUnixTimeSeconds(),
        CertificationExpiresAtUnixSeconds = provider.CertificationExpiresAt?.ToUnixTimeSeconds() ?? 0,
        SupportsBuilderWorkloads = provider.SupportsBuilderWorkloads,
        SupportsRuntimeWorkloads = provider.SupportsRuntimeWorkloads,
        IsAvailable = provider.IsAvailable,
        UnavailableReason = provider.UnavailableReason ?? string.Empty
    };

    private static OfficeSecurityPosture ProviderInventory(OfficeSecurityPostureReport report)
    {
        var posture = new OfficeSecurityPosture
        {
            Profile = report.Profile,
            MixedUseHost = report.MixedUseHost,
            DevelopmentAssignmentsAllowed = report.DevelopmentAssignmentsAllowed,
            EvaluatedAtUnixSeconds = report.EvaluatedAt.ToUnixTimeSeconds()
        };
        posture.EnabledControls.AddRange(report.EnabledControls);
        posture.MissingControls.AddRange(report.MissingControls);
        return posture;
    }

    private async Task ReadControlMessagesAsync(
        IAsyncStreamReader<HeadquartersControlMessage> stream,
        IClientStreamWriter<OfficeControlMessage> writer,
        SemaphoreSlim writerLock,
        OfficeGateway.OfficeGatewayClient gatewayClient,
        OfficeState state,
        CancellationToken cancellationToken)
    {
        using var assignmentVerificationKey = ECDsa.Create();
        var assignmentPublicKey = Convert.FromBase64String(state.AssignmentVerificationPublicKeyBase64);
        assignmentVerificationKey.ImportSubjectPublicKeyInfo(assignmentPublicKey, out var importedBytes);
        if (importedBytes != assignmentPublicKey.Length)
            throw new InvalidDataException("The pinned assignment verification key contains trailing data.");
        var assignmentSigningKeyId = state.AssignmentSigningKeyId;
        try
        {
        await foreach (var message in stream.ReadAllAsync(cancellationToken))
        {
            if (message.OfficeId != state.OfficeId.ToString("D") || message.SessionEpoch != state.SessionEpoch)
                throw new InvalidDataException("The control-plane message was not bound to this node session.");
            if (message.BodyCase == HeadquartersControlMessage.BodyOneofCase.Hello)
            {
                if (message.Hello.AssignmentVerificationPublicKey.Length is < 64 or > 1024 ||
                    string.IsNullOrWhiteSpace(message.Hello.AssignmentSigningKeyId))
                    throw new InvalidDataException("The gateway signing identity is invalid.");
                if (!string.Equals(message.Hello.AssignmentSigningKeyId, assignmentSigningKeyId, StringComparison.Ordinal) ||
                    !CryptographicOperations.FixedTimeEquals(
                        message.Hello.AssignmentVerificationPublicKey.Span, assignmentPublicKey))
                    throw new InvalidDataException("The gateway signing identity does not match the identity pinned during enrollment.");
            }
            else if (message.BodyCase == HeadquartersControlMessage.BodyOneofCase.Assignment)
            {
                try
                {
                    ValidateAssignment(message.Assignment, state.OfficeId,
                        assignmentVerificationKey, assignmentSigningKeyId);
                }
                catch (InvalidDataException exception)
                {
                    logger.LogError(exception,
                        "Rejected assignment {AssignmentId} epoch {FencingEpoch} before execution.",
                        message.Assignment.AssignmentId,
                        message.Assignment.FencingEpoch);
                    await SendStatusAsync(writer, writerLock, state, message.Assignment,
                        "Failed", "assignment-envelope-invalid",
                        "The Office rejected the signed assignment envelope. " +
                        "Verify that headquarters and Office use compatible contract versions.",
                        null, null, cancellationToken);
                    continue;
                }
                logger.LogInformation("Received fenced workload assignment {AssignmentId} at epoch {Epoch}.",
                    message.Assignment.AssignmentId, message.Assignment.FencingEpoch);
                var assignmentId = Guid.Parse(message.Assignment.AssignmentId);
                var assignmentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_activeAssignments.TryAdd(assignmentId, assignmentCancellation))
                {
                    stateStore.MarkAssignmentActive(assignmentId);
                    _ = ExecuteAssignmentAsync(message.Assignment, writer, writerLock, gatewayClient, state,
                        assignmentCancellation).ContinueWith(task =>
                        {
                            if (task.Exception is not null)
                                logger.LogError(task.Exception, "Execution assignment {AssignmentId} failed.", assignmentId);
                        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                else
                    assignmentCancellation.Dispose();
            }
            else if (message.BodyCase == HeadquartersControlMessage.BodyOneofCase.Fence)
            {
                if (Guid.TryParse(message.Fence.AssignmentId, out var fencedId) &&
                    _activeAssignments.TryGetValue(fencedId, out var active)) active.Cancel();
                logger.LogWarning("Assignment {AssignmentId} was fenced at epoch {Epoch}: {Reason}",
                    message.Fence.AssignmentId, message.Fence.FencingEpoch, message.Fence.Reason);
            }
            else if (message.BodyCase == HeadquartersControlMessage.BodyOneofCase.Drain)
            {
                stateStore.SetDraining(message.Drain.Drain);
                logger.LogWarning("The node drain state changed to {Drain}: {Reason}",
                    message.Drain.Drain, message.Drain.Reason);
            }
        }
        }
        finally { }
    }

    private async Task ExecuteAssignmentAsync(
        WorkloadAssignment assignment,
        IClientStreamWriter<OfficeControlMessage> writer,
        SemaphoreSlim writerLock,
        OfficeGateway.OfficeGatewayClient gatewayClient,
        OfficeState state,
        CancellationTokenSource assignmentCancellation)
    {
        var assignmentId = Guid.Parse(assignment.AssignmentId);
        IsolationWorkloadHandle? handle = null;
        IAgentIsolationProvider? provider = null;
        CancellationTokenSource? tunnelLifetime = null;
        Task? tunnelTask = null;
        var enteredSlot = false;
        try
        {
            await _workloadSlots.WaitAsync(assignmentCancellation.Token);
            enteredSlot = true;
            if (!_providers.TryGetValue(assignment.ProviderId, out provider))
                throw new IsolationUnavailableException("The assigned provider is not installed on this node.");
            var specification = DeserializeSpecification(assignment.SpecificationJson);
            if (specification is RuntimeWorkloadSpecification runtime)
            {
                if (assignment.ArtifactReadToken.Length is < 32 or > 256)
                    throw new InvalidDataException("The runtime assignment does not contain a valid artifact read grant.");
                await artifactCache.EnsureAsync(
                    gatewayClient, state, assignment, runtime.Artifact.Digest, assignmentCancellation.Token);
            }
            await SendStatusAsync(writer, writerLock, state, assignment,
                "Starting", null, null, null, null, assignmentCancellation.Token);
            if (provider is not IRuntimeHostClient runtimeHost)
                throw new IsolationUnavailableException("The assigned provider does not enforce signed workload authorization.");
            handle = await runtimeHost.CreateAuthorizedAsync(specification,
                ToAuthorization(state, assignment), assignmentCancellation.Token);
            await provider.StartAsync(handle, assignmentCancellation.Token);
            if (provider is not IAgentGuestChannelProvider guestChannels)
                throw new IsolationUnavailableException("The RuntimeHost provider does not expose a guest broker channel.");
            logger.LogInformation(
                "Assignment {AssignmentId} epoch {FencingEpoch} started provider {ProviderId}; waiting for the authenticated guest broker channel.",
                assignmentId, assignment.FencingEpoch, assignment.ProviderId);
            var guestStream = await guestChannels.OpenGuestChannelAsync(handle, assignmentCancellation.Token);
            logger.LogInformation(
                "Assignment {AssignmentId} epoch {FencingEpoch} opened the authenticated guest broker channel.",
                assignmentId, assignment.FencingEpoch);
            tunnelLifetime = CancellationTokenSource.CreateLinkedTokenSource(assignmentCancellation.Token);
            tunnelTask = RelayGuestChannelAsync(
                gatewayClient, guestStream, state, assignment, tunnelLifetime.Token);
            await SendStatusAsync(writer, writerLock, state, assignment,
                "Running", null, null, handle, null, assignmentCancellation.Token);

            var nextRenewal = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!assignmentCancellation.IsCancellationRequested)
            {
                if (tunnelTask.IsFaulted) await tunnelTask;
                if (DateTimeOffset.UtcNow >= nextRenewal)
                {
                    await SendAsync(writer, writerLock, Envelope(state, new AssignmentLeaseRenewal
                    {
                        AssignmentId = assignment.AssignmentId,
                        FencingEpoch = assignment.FencingEpoch,
                        RequestedExpiryUnixSeconds = DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds()
                    }), assignmentCancellation.Token);
                    nextRenewal = DateTimeOffset.UtcNow.AddSeconds(20);
                }
                var status = await provider.InspectAsync(handle, assignmentCancellation.Token);
                if (status is null || status.State is IsolationWorkloadState.Destroyed or
                    IsolationWorkloadState.Failed or IsolationWorkloadState.Stopped)
                {
                    var completed = status is not null && status.State != IsolationWorkloadState.Failed &&
                        status.ExitCode.GetValueOrDefault() == 0 &&
                        status.TerminationReason is IsolationTerminationReason.None or IsolationTerminationReason.Completed;
                    var logs = await ReadLogsAsync(provider, handle, CancellationToken.None);
                    await SendStatusAsync(writer, writerLock, state, assignment,
                        completed ? "Completed" : "Failed",
                        completed ? null : status?.ErrorCode ?? "workload-failed",
                        completed ? null : "The isolated workload did not complete successfully.",
                        handle,
                        logs,
                        assignmentCancellation.Token);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), assignmentCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (assignmentCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Assignment {AssignmentId} epoch {FencingEpoch} failed while executing provider {ProviderId}.",
                assignmentId, assignment.FencingEpoch, assignment.ProviderId);
            try
            {
                var failure = DescribeExecutionFailure(exception);
                await SendStatusAsync(writer, writerLock, state, assignment,
                    "Failed", failure.FailureCode, failure.SanitizedFailure,
                    handle,
                    provider is not null && handle is not null
                        ? await ReadLogsAsync(provider, handle, CancellationToken.None)
                        : null,
                    CancellationToken.None);
            }
            catch (Exception reportException)
            {
                logger.LogWarning(reportException, "Could not report assignment {AssignmentId} failure.", assignmentId);
            }
        }
        finally
        {
            if (tunnelLifetime is not null)
            {
                await tunnelLifetime.CancelAsync();
                if (tunnelTask is not null)
                {
                    try { await tunnelTask; }
                    catch (OperationCanceledException) when (tunnelLifetime.IsCancellationRequested) { }
                    catch (IOException) when (tunnelLifetime.IsCancellationRequested) { }
                    catch (RpcException) when (tunnelLifetime.IsCancellationRequested) { }
                }
                tunnelLifetime.Dispose();
            }
            if (handle is not null && provider is not null)
            {
                try { await provider.DestroyAsync(handle, CancellationToken.None); }
                catch (Exception exception) { logger.LogWarning(exception, "Could not destroy workload {AssignmentId}.", assignmentId); }
            }
            if (enteredSlot) _workloadSlots.Release();
            _activeAssignments.TryRemove(assignmentId, out _);
            stateStore.MarkAssignmentInactive(assignmentId);
            assignmentCancellation.Dispose();
        }
    }

    private static async Task RelayGuestChannelAsync(
        OfficeGateway.OfficeGatewayClient client,
        Stream guest,
        OfficeState state,
        WorkloadAssignment assignment,
        CancellationToken cancellationToken)
    {
        await using (guest)
        using (var call = client.OpenWorkloadTunnel(cancellationToken: cancellationToken))
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // OpenWorkloadTunnel cannot start the Headquarters broker session until it receives
            // the first bound frame. The Linux guest waits for Headquarters to send its boot
            // configuration before it writes anything, so waiting for guest bytes here creates
            // a three-way deadlock. Send an explicit empty opening frame before starting either
            // relay direction; it carries no guest data and is consumed as sequence zero.
            await call.RequestStream.WriteAsync(new WorkloadTunnelFrame
            {
                OfficeId = state.OfficeId.ToString("D"),
                AssignmentId = assignment.AssignmentId,
                FencingEpoch = assignment.FencingEpoch,
                SessionEpoch = state.SessionEpoch,
                Sequence = 0,
                Content = Google.Protobuf.ByteString.Empty,
                Completed = false
            }, cancellationToken);

            var upload = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                long sequence = 1;
                while (true)
                {
                    var read = await guest.ReadAsync(buffer, lifetime.Token);
                    await call.RequestStream.WriteAsync(new WorkloadTunnelFrame
                    {
                        OfficeId = state.OfficeId.ToString("D"),
                        AssignmentId = assignment.AssignmentId,
                        FencingEpoch = assignment.FencingEpoch,
                        SessionEpoch = state.SessionEpoch,
                        Sequence = sequence++,
                        Content = read == 0
                            ? Google.Protobuf.ByteString.Empty
                            : Google.Protobuf.ByteString.CopyFrom(buffer, 0, read),
                        Completed = read == 0
                    }, lifetime.Token);
                    if (read == 0) break;
                }
                await call.RequestStream.CompleteAsync();
            }, lifetime.Token);
            var download = Task.Run(async () =>
            {
                long expectedSequence = 0;
                await foreach (var frame in call.ResponseStream.ReadAllAsync(lifetime.Token))
                {
                    if (frame.Sequence != expectedSequence++ || frame.FencingEpoch != assignment.FencingEpoch)
                        throw new InvalidDataException("The gateway returned an invalid guest-channel frame sequence.");
                    if (frame.Content.Length > 0)
                        await guest.WriteAsync(frame.Content.Memory, lifetime.Token);
                    if (frame.Completed) break;
                }
            }, lifetime.Token);
            var completed = await Task.WhenAny(upload, download);
            await completed;
            if (upload.IsCompletedSuccessfully) await download;
            await lifetime.CancelAsync();
        }
    }

    private static WorkloadSpecification DeserializeSpecification(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 32 };
        var isBuilder = document.RootElement.EnumerateObject()
            .Any(x => x.Name.Equals("repository", StringComparison.OrdinalIgnoreCase));
        return isBuilder
            ? JsonSerializer.Deserialize<BuilderWorkloadSpecification>(json, options)
                ?? throw new InvalidDataException("The builder workload specification is empty.")
            : JsonSerializer.Deserialize<RuntimeWorkloadSpecification>(json, options)
                ?? throw new InvalidDataException("The runtime workload specification is empty.");
    }

    internal static (string FailureCode, string SanitizedFailure) DescribeExecutionFailure(
        Exception exception)
    {
        if (exception is IsolationUnavailableException)
            return ("isolation-provider-unavailable", SanitizeFailureDetail(exception.Message));

        if (exception is RpcException rpc)
        {
            return rpc.StatusCode switch
            {
                StatusCode.FailedPrecondition => (
                    "headquarters-broker-rejected",
                    "Headquarters rejected the authenticated guest broker session: " +
                    SanitizeFailureDetail(rpc.Status.Detail)),
                StatusCode.PermissionDenied or StatusCode.Unauthenticated => (
                    "headquarters-authorization-rejected",
                    "Headquarters rejected the Office authorization."),
                StatusCode.Unavailable or StatusCode.DeadlineExceeded => (
                    "headquarters-unavailable",
                    "The secure connection to Headquarters was unavailable while the workload was running."),
                _ => (
                    "headquarters-rpc-error",
                    $"The secure Headquarters connection failed ({rpc.StatusCode}).")
            };
        }

        return ("office-error",
            $"The Office could not execute the workload ({exception.GetType().Name}). " +
            "Review the Office Node service log using the assignment identifier.");
    }

    private static string SanitizeFailureDetail(string value)
    {
        var sanitized = new string(value
            .Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
            .Take(1500)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized)
            ? "No additional detail was provided."
            : sanitized;
    }

    private static Task SendStatusAsync(
        IClientStreamWriter<OfficeControlMessage> writer,
        SemaphoreSlim writerLock,
        OfficeState state,
        WorkloadAssignment assignment,
        string status,
        string? failureCode,
        string? sanitizedFailure,
        IsolationWorkloadHandle? handle,
        string? logExcerpt,
        CancellationToken cancellationToken) => SendAsync(writer, writerLock,
            Envelope(state, new AssignmentStatusUpdate
            {
                AssignmentId = assignment.AssignmentId,
                FencingEpoch = assignment.FencingEpoch,
                Status = status,
                FailureCode = failureCode ?? string.Empty,
                SanitizedFailure = sanitizedFailure ?? string.Empty,
                ProviderInstanceId = handle?.ProviderInstanceId ?? string.Empty,
                LogExcerpt = logExcerpt ?? string.Empty
            }), cancellationToken);

    private static async Task<string?> ReadLogsAsync(
        IAgentIsolationProvider provider,
        IsolationWorkloadHandle handle,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await foreach (var chunk in provider.StreamLogsAsync(handle, 64 * 1024, cancellationToken))
        {
            var remaining = 64 * 1024 - (int)output.Length;
            if (remaining <= 0) break;
            await output.WriteAsync(chunk.Content[..Math.Min(remaining, chunk.Content.Length)], cancellationToken);
        }
        return output.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static OfficeControlMessage Envelope(OfficeState state, AssignmentLeaseRenewal renewal) => new()
    {
        ProtocolVersion = "1.0", OfficeId = state.OfficeId.ToString("D"),
        SessionEpoch = state.SessionEpoch, LeaseRenewal = renewal
    };

    private static OfficeControlMessage Envelope(OfficeState state, AssignmentStatusUpdate status) => new()
    {
        ProtocolVersion = "1.0", OfficeId = state.OfficeId.ToString("D"),
        SessionEpoch = state.SessionEpoch, AssignmentStatus = status
    };

    private static async Task SendAsync(
        IClientStreamWriter<OfficeControlMessage> writer,
        SemaphoreSlim writerLock,
        OfficeControlMessage message,
        CancellationToken cancellationToken)
    {
        await writerLock.WaitAsync(cancellationToken);
        try { await writer.WriteAsync(message, cancellationToken); }
        finally { writerLock.Release(); }
    }

    private static void ValidateAssignment(
        WorkloadAssignment assignment,
        Guid nodeId,
        ECDsa verificationKey,
        string keyId)
    {
        if (assignment.AuthorizationVersion != AssignmentEnvelope.CurrentAuthorizationVersion ||
            !string.Equals(keyId, assignment.SignatureKeyId, StringComparison.Ordinal) ||
            !Guid.TryParse(assignment.AssignmentId, out var assignmentId) ||
            !Guid.TryParse(assignment.WorkloadId, out var workloadId) ||
            assignmentId == Guid.Empty || workloadId == Guid.Empty || assignment.FencingEpoch < 1 ||
            string.IsNullOrWhiteSpace(assignment.ProviderId))
            throw new InvalidDataException("The assignment signing identity is unavailable.");
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(assignment.IssuedAtUnixSeconds);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(assignment.LeaseExpiresAtUnixSeconds);
        if (issuedAt > DateTimeOffset.UtcNow.AddMinutes(2) || expiresAt <= DateTimeOffset.UtcNow ||
            expiresAt - issuedAt > TimeSpan.FromMinutes(10))
            throw new InvalidDataException(
                $"Assignment {assignmentId:D} epoch {assignment.FencingEpoch} is outside its authorization lifetime.");
        var expectedDigest = AssignmentEnvelope.Digest(assignment.SpecificationJson);
        if (!string.Equals(expectedDigest, assignment.SpecificationSha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Assignment {assignmentId:D} epoch {assignment.FencingEpoch} has a specification digest mismatch " +
                $"(expected {expectedDigest}, received {assignment.SpecificationSha256}).");
        if (!verificationKey.VerifyData(
                AssignmentEnvelope.Payload(nodeId, assignmentId, workloadId, assignment.FencingEpoch,
                    assignment.ProviderId, assignment.SpecificationSha256, issuedAt, expiresAt),
                assignment.Signature.Span, HashAlgorithmName.SHA256))
            throw new InvalidDataException(
                $"Assignment {assignmentId:D} epoch {assignment.FencingEpoch} has an invalid signature for key {keyId}.");
    }

    private static CSweet.Office.Runtime.Abstractions.SignedWorkloadAuthorization ToAuthorization(
        OfficeState state,
        WorkloadAssignment assignment) => new(
            assignment.AuthorizationVersion,
            state.OfficeId,
            Guid.Parse(assignment.AssignmentId),
            Guid.Parse(assignment.WorkloadId),
            assignment.FencingEpoch,
            assignment.ProviderId,
            assignment.SpecificationJson,
            assignment.SpecificationSha256,
            assignment.SignatureKeyId,
            assignment.Signature.ToByteArray(),
            DateTimeOffset.FromUnixTimeSeconds(assignment.IssuedAtUnixSeconds),
            DateTimeOffset.FromUnixTimeSeconds(assignment.LeaseExpiresAtUnixSeconds));

    private static string Normalize(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}
