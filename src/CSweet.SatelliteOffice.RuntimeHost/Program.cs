using CSweet.SatelliteOffice.Runtime.Abstractions;
using CSweet.SatelliteOffice.Runtime.AppleVirtualization;
using CSweet.SatelliteOffice.Runtime.Firecracker;
using CSweet.SatelliteOffice.Runtime.HyperV;
using CSweet.SatelliteOffice.Runtime.LocalRpc;
using CSweet.SatelliteOffice.Runtime.Protocol;
using CSweet.SatelliteOffice.Runtime.Core;
using CSweet.SatelliteOffice.RuntimeHost;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "CSweet.SatelliteOffice.RuntimeHost");
builder.Services.AddSystemd();

var endpoint = builder.Configuration
    .GetSection(RuntimeHostEndpointOptions.SectionName)
    .Get<RuntimeHostEndpointOptions>() ?? new RuntimeHostEndpointOptions();
endpoint.Validate();
var authentication = builder.Configuration
    .GetSection(RuntimeHostAuthenticationOptions.SectionName)
    .Get<RuntimeHostAuthenticationOptions>() ?? new RuntimeHostAuthenticationOptions();
var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
authentication.LoadSharedKeyFileIfNeeded(Path.Combine(
    string.IsNullOrWhiteSpace(commonData) ? AppContext.BaseDirectory : commonData,
    "CSweet", "SatelliteOffice", "runtime-host.key"));

builder.Services.AddSingleton(endpoint);
builder.Services.AddSingleton(authentication);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RuntimeHostRequestAuthenticator>();
var authorization = builder.Configuration
    .GetSection(RuntimeHostAuthorizationOptions.SectionName)
    .Get<RuntimeHostAuthorizationOptions>() ?? new RuntimeHostAuthorizationOptions();
builder.Services.AddSingleton(authorization);
builder.Services.AddSingleton<RuntimeHostAuthorizationGate>();
builder.Services.AddSingleton<RuntimeHostRequestDispatcher>();
builder.Services.AddSingleton<RuntimeHostRpcServer>();
builder.Services.AddHostedService<RuntimeHostWorker>();
builder.Services.AddHostedService<RuntimeHostWorkloadReaper>();

var hyperV = builder.Configuration.GetSection("CSweet:SatelliteOffice:Providers:HyperV")
    .Get<HyperVIsolationBackendOptions>() ?? new HyperVIsolationBackendOptions();
var firecracker = builder.Configuration.GetSection("CSweet:SatelliteOffice:Providers:Firecracker")
    .Get<FirecrackerIsolationBackendOptions>() ?? new FirecrackerIsolationBackendOptions();
var apple = builder.Configuration.GetSection("CSweet:SatelliteOffice:Providers:AppleVirtualization")
    .Get<AppleVirtualizationIsolationBackendOptions>() ?? new AppleVirtualizationIsolationBackendOptions();
PlatformRuntimePayloadManifest.ApplyIfConfigured(firecracker, IsolationProviderCatalog.Firecracker());
PlatformRuntimePayloadManifest.ApplyIfConfigured(apple, IsolationProviderCatalog.AppleVirtualization());
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton(hyperV);
    builder.Services.AddSingleton<IPlatformIsolationBackend, HyperVIsolationBackend>();
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton(firecracker);
    builder.Services.AddSingleton<IPlatformIsolationBackend, FirecrackerIsolationBackend>();
}
else if (OperatingSystem.IsMacOS())
{
    builder.Services.AddSingleton(apple);
    builder.Services.AddSingleton<IPlatformIsolationBackend, AppleVirtualizationIsolationBackend>();
}
var hyperVSocket = builder.Configuration.GetSection("CSweet:SatelliteOffice:RuntimeHost:HyperVSocket")
    .Get<HyperVSocketTransportOptions>() ?? new HyperVSocketTransportOptions();
hyperVSocket.Validate();
builder.Services.AddSingleton(hyperVSocket);
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<WindowsHyperVSocketTransport>();
    builder.Services.AddSingleton<IHyperVGuestTransport>(services =>
        services.GetRequiredService<WindowsHyperVSocketTransport>());
    builder.Services.AddSingleton<IPlatformGuestChannelConnector>(services =>
        services.GetRequiredService<WindowsHyperVSocketTransport>());
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<IPlatformGuestChannelConnector, FirecrackerGuestChannelConnector>();
}
else if (OperatingSystem.IsMacOS())
{
    builder.Services.AddSingleton<IPlatformGuestChannelConnector, AppleVirtualizationGuestChannelConnector>();
}

await builder.Build().RunAsync();
