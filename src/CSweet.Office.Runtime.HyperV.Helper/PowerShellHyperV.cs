namespace CSweet.Office.Runtime.HyperV.Helper;

internal static class PowerShellHyperV
{
    public static Task<string> RunAsync(string script, IReadOnlyDictionary<string,string>? environment = null) =>
        Adapt(() => CSweet.Isolation.HyperV.PowerShellHyperV.RunAsync(script, environment));
    public static Task<string> CreateShellAsync(string vmName, string vmPath, int memoryMegabytes) =>
        Adapt(() => CSweet.Isolation.HyperV.PowerShellHyperV.CreateShellAsync(vmName, vmPath, memoryMegabytes));
    public static Task<string> ConfigureAsync(string vmName, string baseDisk, string osDisk, string scratchDisk,
        int cpuCount, int cpuPercent, int memoryMegabytes, int scratchMegabytes, string? artifactImagePath) =>
        Adapt(() => CSweet.Isolation.HyperV.PowerShellHyperV.ConfigureAsync(vmName, baseDisk, osDisk, scratchDisk,
            cpuCount, cpuPercent, memoryMegabytes, scratchMegabytes, artifactImagePath));
    public static Task<string> StartAsync(string vmName) => Adapt(() => CSweet.Isolation.HyperV.PowerShellHyperV.StartAsync(vmName));
    public static Task<string> StopAsync(string vmName) => Adapt(() => CSweet.Isolation.HyperV.PowerShellHyperV.StopAsync(vmName));
    public static Task<string> DestroyAsync(string vmName) => Adapt(() => CSweet.Isolation.HyperV.PowerShellHyperV.DestroyAsync(vmName));
    public static Task<string> GetStateAsync(string vmName) => Adapt(() => CSweet.Isolation.HyperV.PowerShellHyperV.GetStateAsync(vmName));
    internal static string Sanitize(string value) => CSweet.Isolation.HyperV.PowerShellHyperV.Sanitize(value);
    private static async Task<string> Adapt(Func<Task<string>> action)
    {
        try { return await action(); }
        catch (CSweet.Isolation.HyperV.HyperVCommandException error)
        { throw new HyperVCommandException(error.ErrorCode, error.Message); }
    }
}
internal sealed class HyperVCommandException(string errorCode, string message) : Exception(message)
{ public string ErrorCode { get; } = errorCode; }
