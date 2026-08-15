using CSweet.Office.Runtime.LocalRpc;
using System.Security.Principal;

namespace CSweet.Office.RuntimeHost;

public sealed class RuntimeHostWorker(
    RuntimeHostRpcServer server,
    ILogger<RuntimeHostWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting the privileged C-Sweet runtime host service.");
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            logger.LogInformation(
                "RuntimeHost is running as Windows identity {RuntimeHostIdentity}. " +
                "Provider readiness will verify effective Hyper-V access with a bounded host probe.",
                identity.Name);
        }
        await server.RunAsync(stoppingToken);
    }
}
