using CSweet.Office.Runtime.Abstractions;
using CSweet.Office.Runtime.LocalRpc;
using CSweet.Office.Runtime.Protocol;
using CSweet.Office.Node;

if (await ControlPlaneCertificateProbe.TryRunAsync(args) is { } probeExitCode)
{
    Environment.ExitCode = probeExitCode;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "CSweet.Office.Node");
builder.Services.AddSystemd();

var options = builder.Configuration.GetSection(OfficeOptions.SectionName)
    .Get<OfficeOptions>() ?? new OfficeOptions();
if (!Uri.TryCreate(options.ControlPlaneUrl, UriKind.Absolute, out var controlPlane) ||
    controlPlane.Scheme != Uri.UriSchemeHttps)
    throw new InvalidOperationException("CSweet:Office:Node:ControlPlaneUrl must be an absolute HTTPS URL.");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ControlPlaneServerCertificateValidator>();
builder.Services.AddSingleton<OfficeStateStore>();
builder.Services.AddSingleton<RuntimeHostInventory>();
builder.Services.AddSingleton<OfficeArtifactCache>();
builder.Services.AddHostedService<OfficeWorker>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("control-plane", client => client.BaseAddress = controlPlane)
    .ConfigurePrimaryHttpMessageHandler(services =>
        services.GetRequiredService<ControlPlaneServerCertificateValidator>().CreateHttpClientHandler());

var endpoint = builder.Configuration.GetSection(RuntimeHostEndpointOptions.SectionName)
    .Get<RuntimeHostEndpointOptions>() ?? new RuntimeHostEndpointOptions();
endpoint.Validate();
builder.Services.AddSingleton(endpoint);
var authentication = builder.Configuration.GetSection(RuntimeHostAuthenticationOptions.SectionName)
    .Get<RuntimeHostAuthenticationOptions>() ?? new RuntimeHostAuthenticationOptions();
var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
authentication.LoadSharedKeyFileIfNeeded(Path.Combine(
    string.IsNullOrWhiteSpace(common) ? AppContext.BaseDirectory : common,
    "CSweet", "Office", "runtime-host.key"));
builder.Services.AddSingleton(authentication);
builder.Services.AddSingleton<RuntimeHostRequestAuthenticator>();
var hostProvider = HostPlatformProvider.Resolve();
builder.Services.AddSingleton<IAgentIsolationProvider>(services => CreateClient(hostProvider, services));

await builder.Build().RunAsync();

static RuntimeHostProviderClient CreateClient(IsolationProviderDescriptor descriptor, IServiceProvider services) =>
    new(descriptor,
        services.GetRequiredService<RuntimeHostEndpointOptions>(),
        services.GetRequiredService<RuntimeHostRequestAuthenticator>(),
        services.GetRequiredService<ILogger<RuntimeHostProviderClient>>());
