using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Office.Contracts.ControlPlane;
using CSweet.Office.Contracts.Security;
using CSweet.Office.Runtime.AppleVirtualization;
using CSweet.Office.Runtime.Core;
using CSweet.Office.Runtime.LocalRpc;
using CSweet.Office.Runtime.Protocol;
using CSweet.Office.RuntimeHost;
using Google.Protobuf;

// All paths belong to the disposable root-owned directory created by the hardware runner.
if (!OperatingSystem.IsMacOS() || args.Length != 1) return 2;
var root = Path.GetFullPath(args[0]);
string FileAt(string name) => Path.Combine(root, name);
string DigestFile(string path)
{
    using var stream = File.OpenRead(path);
    return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
}
static string Json<T>(T value) => JsonSerializer.Serialize(value, ComputeProtocol.Json);
var evidence = JsonSerializer.Deserialize<ProviderCertificationEvidence>(File.ReadAllText(FileAt("evidence.json")), JsonSerializerOptions.Web)!;
using var imageKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
using var signer = new CertificateRequest("CN=Local Compute acceptance", imageKey, HashAlgorithmName.SHA256)
    .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
File.WriteAllBytes(FileAt("signer.cer"), signer.RawData);
foreach (var name in new[] { "agent.img", "compute.img" })
{
    using var image = File.OpenRead(FileAt(name));
    File.WriteAllBytes(FileAt(name + ".sig"), imageKey.SignHash(SHA256.HashData(image)));
}
var runtime = new AppleVirtualizationIsolationBackendOptions
{
    HelperExecutablePath = FileAt("helper"), HelperExecutableDigest = DigestFile(FileAt("helper")),
    GuestImagePath = FileAt("agent.img"), GuestImageDigest = evidence.GuestImageDigest,
    GuestImageSignaturePath = FileAt("agent.img.sig"), GuestImageSigningCertificateThumbprint = signer.Thumbprint,
    GuestImageSigningCertificatePath = FileAt("signer.cer"), CertifiedAt = evidence.CertifiedAt,
    CertificationExpiresAt = evidence.CertificationExpiresAt, CertificationSuiteVersion = evidence.CertificationSuiteVersion,
    CertificationEvidencePath = FileAt("evidence.json"), CertificationEvidenceDigest = DigestFile(FileAt("evidence.json"))
};
var backend = new AppleVirtualizationIsolationBackend(runtime, TimeProvider.System);
var probe = await backend.ProbeAsync();
if (!probe.IsAvailable) throw new IOException("Installed backend probe: " + probe);
using var headquarters = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var site = Guid.NewGuid();
var organization = Guid.NewGuid();
var installation = Guid.NewGuid();
var environment = Guid.NewGuid();
var storage = new RuntimeHostAuthorizationOptions { StateDirectory = FileAt("authorization") };
var trust = new RuntimeHostAuthorizationGate(storage, TimeProvider.System);
const string keyId = "local-compute-acceptance";
trust.Pin(new(site, keyId, headquarters.ExportSubjectPublicKeyInfo()));
var policy = new OfficeComputeOptions { Enabled = true, OrganizationId = organization, MaximumEnvironments = 1,
    CpuCount = 2, MemoryMiB = 2048, DiskMiB = 20480, ImagePath = FileAt("compute.img"),
    ImageDigest = DigestFile(FileAt("compute.img")), SignaturePath = FileAt("compute.img.sig"), Features = ["docker"] };
var endpoint = new RuntimeHostEndpointOptions { UnixSocketPath = FileAt("runtime.sock"), ConnectTimeoutSeconds = 10 };
var auth = new RuntimeHostAuthenticationOptions { KeyId = "smoke", SharedKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
File.WriteAllText(Path.Combine(storage.StateDirectory, "compute-policy.json"), JsonSerializer.Serialize(policy));
File.WriteAllText(FileAt("appsettings.json"), JsonSerializer.Serialize(new {
    Logging = new { LogLevel = new { Default = "Warning" } },
    CSweet = new { Office = new {
        Providers = new { AppleVirtualization = runtime },
        RuntimeHost = new { endpoint.UnixSocketPath, endpoint.ConnectTimeoutSeconds, Authorization = storage, Authentication = auth }
    } }
}));
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
var ct = deadline.Token;
await using var host = new SmokeRuntimeHost(root, endpoint.UnixSocketPath);
await host.StartAsync(ct);
var client = new RuntimeHostProviderClient(backend.Descriptor, endpoint, new(auth, TimeProvider.System));
var inventory = await client.ExchangeComputeAsync(new() { Inventory = true }, ct);
var offer = OfficeComputeOffer.Parser.ParseFrom(inventory.CapacityOffer);
if (offer.ImageDigest != policy.ImageDigest || offer.OrganizationId != organization.ToString("D"))
    throw new IOException("Missing signed-image capacity offer.");
var template = new ComputeTemplate(offer.TemplateId, "linux", "arm64", policy.ImageDigest, ["docker"]);
var spec = new ComputeSpecification("linux", "arm64", template.Id, new(2, 2048, 20480), 1800);
var lease = DateTimeOffset.UtcNow.AddMinutes(20);
long generation = 0;
string? resource = null;
var checks = new List<string> { "certified Office provider", "authenticated RuntimeHost RPC", "signed Compute image offered" };
const string provider = "office-apple-virtualization";

ComputeExchange Packet(string action, Guid operation, ComputeDispatchMode mode, ComputeWorkload? work = null)
{
    var now = DateTimeOffset.UtcNow;
    var expires = now.AddSeconds(55);
    var selected = action == InfrastructureActions.Provision && mode == ComputeDispatchMode.Execute ? template : null;
    var claim = new ComputeDispatchAuthorization(Guid.NewGuid(), operation, environment, organization, installation, site,
        provider, generation, action, mode, ComputeProtocol.Digest(Json(spec)), selected is null ? null : ComputeProtocol.Digest(Json(selected)),
        resource, now, expires, lease, mode == ComputeDispatchMode.Observe ? [] : [new(Guid.NewGuid(), 1, action, expires)],
        work is null ? null : ComputeProtocol.Digest(Json(work)));
    var json = Json(claim);
    var sig = headquarters.SignData(ComputeProtocol.DispatchPayload(json), HashAlgorithmName.SHA256);
    var packet = new ComputeDispatchPacket(new(keyId, json, Convert.ToBase64String(sig)), spec, selected, work);
    return new() { SignedPacket = ByteString.CopyFromUtf8(Json(packet)) };
}
async Task<ComputeProviderResult> Dispatch(ComputeExchange packet, Guid operation)
{
    await client.ExchangeComputeAsync(packet, ct);
    var entry = (await client.ExchangeComputeAsync(new() { Inventory = true }, ct)).ResultPayloads.Single();
    var result = JsonSerializer.Deserialize<ComputeProviderResult>(entry.Span, ComputeProtocol.Json)!;
    if (result.OperationId != operation || result.EnvironmentId != environment || result.ProviderId != provider)
        throw new IOException("Returned evidence is for another environment or operation.");
    var digest = ComputeProtocol.Digest(entry.ToStringUtf8());
    var receipt = new OfficeComputeReceipt { OperationId = operation.ToString("D"), Sequence = result.Sequence, PayloadDigest = digest,
        Signature = ByteString.CopyFrom(headquarters.SignData(OfficeComputeReceiptEncoding.Payload(site, operation, result.Sequence, digest), HashAlgorithmName.SHA256)) };
    await client.ExchangeComputeAsync(new() { SignedReceipt = receipt.ToByteString() }, ct);
    resource ??= result.ResourceId;
    return result;
}
async Task Lifecycle(string action, ComputeLifecycleState expected)
{
    generation++;
    var operation = Guid.NewGuid();
    var result = await Dispatch(Packet(action, operation, ComputeDispatchMode.Execute), operation);
    while (result.State == ComputeLifecycleState.Bootstrapping && expected == ComputeLifecycleState.Ready)
    {
        await Task.Delay(1000, ct);
        result = await Dispatch(Packet(action, operation, ComputeDispatchMode.Observe), operation);
    }
    if (result.State != expected || result.TeardownConfirmed != (expected == ComputeLifecycleState.Destroyed))
        throw new IOException($"Unexpected {action} result {Json(result)}");
    if (expected == ComputeLifecycleState.Destroyed)
    {
        var observed = await Dispatch(Packet(action, operation, ComputeDispatchMode.Observe), operation);
        if (!observed.TeardownConfirmed || observed.ResourceId != resource) throw new IOException("Removal observation failed.");
    }
    checks.Add(action + " " + expected);
    Console.WriteLine(checks[^1]);
}
async Task Command(string script, string expected, bool replay = false)
{
    generation++;
    var operation = Guid.NewGuid();
    var work = new ComputeWorkload(new(Guid.NewGuid(), "/bin/sh", "/var/lib/csweet-compute/work", ["-ec", script], 20, 8192));
    var packet = Packet(InfrastructureActions.Execute, operation, ComputeDispatchMode.Execute, work);
    var result = await Dispatch(packet, operation);
    if (result.Workload?.Command is not { ExitCode: 0 } command || Encoding.UTF8.GetString(command.StandardOutput).Trim() != expected)
        throw new IOException("Guest command failed: " + Json(result));
    if (replay)
    {
        var second = await Dispatch(packet, operation);
        if (second.Workload?.ErrorCode != "outcome-unknown") throw new IOException("Acknowledged command replay was admitted.");
        checks.Add("duplicate signed command fenced");
    }
    checks.Add(expected);
    Console.WriteLine(expected);
}
try
{
    await Lifecycle(InfrastructureActions.Provision, ComputeLifecycleState.Ready);
    await Command("test \"$(uname -m)\" = aarch64; test \"$(ls /sys/class/net)\" = lo; tar -C /usr/bin -cf - busybox | docker import - csweet-smoke:local >/dev/null; test \"$(docker run --pull=never --rm --network=none csweet-smoke:local /busybox echo container-ok)\" = container-ok; echo mark >> marker; sync; echo guest-ok", "guest-ok", replay: true);
    await Lifecycle(InfrastructureActions.Stop, ComputeLifecycleState.Stopped);
    await host.StopAsync();
    await host.StartAsync(ct);
    checks.Add("RuntimeHost process restarted with retained journal");
    await Lifecycle(InfrastructureActions.Start, ComputeLifecycleState.Ready);
    await Lifecycle(InfrastructureActions.Restart, ComputeLifecycleState.Ready);
    await Command("test \"$(cat marker)\" = mark; echo private-disk-retained", "private-disk-retained");
}
finally { await Lifecycle(InfrastructureActions.Destroy, ComputeLifecycleState.Destroyed); }
if (Directory.EnumerateFileSystemEntries(FileAt("data/instances")).Any() || DigestFile(policy.ImagePath) != policy.ImageDigest)
    throw new IOException("Private disk cleanup or immutable template verification failed.");
checks.Add("private disk removed and template unchanged");
Console.WriteLine(Json(new { provider, architecture = spec.Architecture, imageDigest = policy.ImageDigest, checks }));
return 0;

sealed class SmokeRuntimeHost(string root, string socketPath) : IAsyncDisposable
{
    private Process? process;

    public async Task StartAsync(CancellationToken token)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = root, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CSweet.Office.RuntimeHost.dll"));
        process = Process.Start(start) ?? throw new IOException("RuntimeHost failed to launch.");
        while (!Path.Exists(socketPath))
        {
            if (process.HasExited) throw new IOException($"RuntimeHost exited with code {process.ExitCode}.");
            await Task.Delay(100, token);
        }
    }

    public async Task StopAsync()
    {
        if (process is null) return;
        // A hard RuntimeHost restart exercises the durable journal. The per-VM
        // manager remains outside its process tree and continues owning the disk.
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync();
        process.Dispose();
        process = null;
        File.Delete(socketPath);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
