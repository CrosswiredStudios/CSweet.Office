using System.Text.Json;

namespace CSweet.Office.Node.Desktop;

// This file is readable by desktop users. Never serialize OfficeState, configuration, logs or enrollment credentials here.
internal sealed record OfficeHealth(Guid OfficeId, string Gateway, string Connection, DateTimeOffset CheckedAt,
    int Cpu, int MemoryMb, int DiskMb, int ActiveWork, int WorkLimit)
{
    private static string DirectoryPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CSweet", "OfficeStatus")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/CSweet/OfficeStatus" : "/run/csweet-office-status";

    internal static OfficeHealth? Read()
    {
        try { return JsonSerializer.Deserialize<OfficeHealth>(File.ReadAllText(Path.Combine(DirectoryPath, "health.json"))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static void Publish(OfficeOptions options, OfficeState state, string connection, int active)
    {
        if (!options.PublishDesktopStatus) return;
        var temp = Path.Combine(DirectoryPath, "health.new");
        try
        {
            var health = new OfficeHealth(state.OfficeId, new Uri(options.ControlPlaneUrl).Authority, connection, DateTimeOffset.UtcNow,
                options.AllocatableCpuCount, options.AllocatableMemoryMb, options.AllocatableDiskMb, active, options.MaximumConcurrentWorkloads);
            File.WriteAllText(temp, JsonSerializer.Serialize(health));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temp, Path.Combine(DirectoryPath, "health.json"), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Reporting cannot interrupt the service. */ }
    }
}
