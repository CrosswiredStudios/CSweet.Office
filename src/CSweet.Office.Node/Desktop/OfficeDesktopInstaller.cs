using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;

namespace CSweet.Office.Node.Desktop;

internal sealed record DesktopService(string Name, string State);

internal sealed record DesktopAllocation(int Cpu, int MemoryGiB, int DiskGiB)
{
    internal void Validate()
    {
        if (Cpu < 1 || Cpu > Environment.ProcessorCount || MemoryGiB is < 1 or > 16384 || DiskGiB is < 1 or > 1048576)
            throw new ArgumentException("Choose a CPU count available on this computer and positive memory and storage limits.");
    }
    internal string[] Values => [Cpu.ToString(System.Globalization.CultureInfo.InvariantCulture),
        (MemoryGiB * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
        (DiskGiB * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture)];
}

internal static class OfficeDesktopInstaller
{
    internal static Uri HeadquartersAddress(string address)
    {
        var value = address.Trim();
        return Origin(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value);
    }

    internal static Uri Origin(string address)
    {
        if (address.Length > 512 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/") throw new ArgumentException("Enter the HTTPS address, including its port if needed.");
        return uri;
    }

    internal static bool ValidToken(string? token) => token is { Length: >= 32 and <= 256 } &&
        token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal static async Task<DesktopService[]> ServicesAsync()
    {
        if (OperatingSystem.IsWindows())
            return [WindowsService("Node"), WindowsService("RuntimeHost"), WindowsService("Maintenance")];
        if (OperatingSystem.IsMacOS())
            return [await MacService("Node", "com.csweet.office", "com.csweet.office.node"),
                    await MacService("RuntimeHost", "com.csweet.office.runtime", "com.csweet.office.runtime")];
        return [await LinuxService("Node", "csweet-office-node"), await LinuxService("RuntimeHost", "csweet-office-runtime")];
    }

    [SupportedOSPlatform("windows")]
    private static DesktopService WindowsService(string name)
    {
        var service = "CSweet.Office." + name;
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service);
        if (key == null) return new(name, "Awaiting setup");
        try { using var controller = new ServiceController(service); return new(name, controller.Status.ToString()); }
        catch (InvalidOperationException) { return new(name, "Status unavailable"); }
    }

    private static async Task<DesktopService> LinuxService(string name, string unit)
    {
        if (!File.Exists("/etc/systemd/system/" + unit + ".service")) return new(name, "Awaiting setup");
        var state = await ReadStatus("/usr/bin/systemctl", "is-active", unit);
        return new(name, state.Trim() switch { "active" => "Running", "inactive" => "Stopped", "failed" => "Failed", _ => "Status unavailable" });
    }

    private static async Task<DesktopService> MacService(string name, string label, string plist)
    {
        if (!File.Exists("/Library/LaunchDaemons/" + plist + ".plist")) return new(name, "Awaiting setup");
        var state = await ReadStatus("/bin/launchctl", "print", "system/" + label);
        return new(name, state.Contains("state = running", StringComparison.Ordinal) ? "Running" :
            state.Length > 0 ? "Stopped" : "Status unavailable");
    }

    private static async Task<string> ReadStatus(string command, params string[] args)
    {
        var info = new ProcessStartInfo(command) { RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return await output;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException) { return ""; }
    }

    internal static async Task<int> InstallAsync(Uri gateway, string? pin, string token, string? proofInput, DesktopAllocation? allocation)
    {
        if (!ValidToken(token) || pin is { Length: > 0 } && (pin.Length != 64 || !pin.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Check the connection code and certificate fingerprint.");
        if ((await ServicesAsync()).Any(s => s.State != "Awaiting setup"))
            throw new InvalidOperationException("This Office already has installed services. Open Headquarters to manage its connection.");

        allocation?.Validate();
        var limits = allocation?.Values;
        var protectedInput = proofInput ?? token;
        if (OperatingSystem.IsLinux())
        {
            var info = new ProcessStartInfo("/usr/bin/pkexec") { RedirectStandardInput = true };
            foreach (var arg in new[] { "/usr/sbin/csweet-configure-office", gateway.AbsoluteUri, "--accept-baseline-risk", "--fresh-only" })
                info.ArgumentList.Add(arg);
            if (!string.IsNullOrEmpty(pin)) { info.ArgumentList.Add("--gateway-sha256"); info.ArgumentList.Add(pin); }
            if (limits != null) { info.ArgumentList.Add("--limits"); foreach (var value in limits) info.ArgumentList.Add(value); }
            using var proc = Process.Start(info)!;
            await proc.StandardInput.WriteLineAsync(protectedInput);
            proc.StandardInput.Close();
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }

        var directory = Directory.CreateTempSubdirectory("csweet-office-");
        var input = Path.Combine(directory.FullName, "enrollment");
        try
        {
            await File.WriteAllTextAsync(input, protectedInput + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(input, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            ProcessStartInfo info;
            if (OperatingSystem.IsWindows())
            {
                var root = Registry.LocalMachine.OpenSubKey(@"Software\CSweet\Office\Storage")?.GetValue("InstallRoot") as string ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CSweet", "Office");
                info = new(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "Install-CSweetOffice.ps1"),
                    "-PayloadRoot", Path.Combine(root, "payload"), "-ControlPlaneUrl", gateway.AbsoluteUri, "-EnrollmentTokenInputPath",
                    input, "-FreshOnly", "-NonInteractive", "-Elevated" }) info.ArgumentList.Add(arg);
                if (limits != null)
                    foreach (var arg in new[] { "-AllocatableCpuCount", limits[0], "-AllocatableMemoryMb", limits[1], "-AllocatableDiskMb", limits[2] })
                        info.ArgumentList.Add(arg);
                if (!string.IsNullOrEmpty(pin)) { info.ArgumentList.Add("-ControlPlaneCertificateSha256"); info.ArgumentList.Add(pin); }
            }
            else
            {
                info = new("/usr/bin/osascript");
                foreach (var arg in new[] { "-e", "on run argv", "-e",
                    "set cmd to \"/usr/local/sbin/csweet-configure-office \" & quoted form of (item 1 of argv) & \" --fresh-only\"",
                    "-e", "if (item 3 of argv) is not \"\" then set cmd to cmd & \" --gateway-sha256 \" & quoted form of (item 3 of argv)",
                    "-e", "if (count of argv) is 6 then set cmd to cmd & \" --limits \" & quoted form of (item 4 of argv) & \" \" & quoted form of (item 5 of argv) & \" \" & quoted form of (item 6 of argv)",
                    "-e", "do shell script (cmd & \" < \" & quoted form of (item 2 of argv)) with administrator privileges",
                    "-e", "end run", "--", gateway.AbsoluteUri, input, pin ?? "" }) info.ArgumentList.Add(arg);
                if (limits != null) foreach (var value in limits) info.ArgumentList.Add(value);
            }
            using var process = Process.Start(info)!;
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        finally { File.Delete(input); directory.Delete(); }
    }
}
