using System.Diagnostics;
using System.Text;

namespace CSweet.Office.RuntimeGuest;

public interface IGuestWorkloadSupervisor : IAsyncDisposable
{
    bool IsRunning { get; }
    string? DiagnosticDetail { get; }
    string? ResourceFailureCode => null;
    long PeakMemoryBytes => 0;
    Task StartAsync(IReadOnlyList<string> entrypoint, long maximumLogBytes,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken);
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken);
}

public sealed class GuestWorkloadSupervisor(GuestServiceOptions options) : IGuestWorkloadSupervisor
{
    private readonly string _artifactRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ArtifactRoot));
    private Process? _process;
    private string? _cgroupPath;
    private Task[] _logDrains = [];
    private long _logBytes;
    private readonly object _diagnosticLock = new();
    private readonly StringBuilder _diagnosticTail = new();

    public bool IsRunning => _process is { HasExited: false };
    public string? ResourceFailureCode => _cgroupPath is not null && ReadCounter(Path.Combine(_cgroupPath, "memory.events"), "oom_kill") > 0
        ? "workload-memory-limit-exceeded" : null;
    public long PeakMemoryBytes => _cgroupPath is not null && long.TryParse(File.ReadAllText(Path.Combine(_cgroupPath, "memory.peak")).Trim(), out var bytes) ? bytes : 0;
    public string? DiagnosticDetail
    {
        get
        {
            lock (_diagnosticLock)
                return _diagnosticTail.Length == 0 ? null : _diagnosticTail.ToString();
        }
    }

    public Task StartAsync(IReadOnlyList<string> entrypoint, long maximumLogBytes,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_process is not null) throw new InvalidOperationException("The guest workload can only be started once.");
        if (entrypoint.Count is < 1 or > 64) throw new InvalidDataException("The workload entrypoint is invalid.");
        if (maximumLogBytes is < 1 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumLogBytes));

        var executable = ResolveExecutable(entrypoint[0]);
        var runAsUnprivilegedUser = !OperatingSystem.IsWindows();
        if (OperatingSystem.IsLinux() && options.WorkloadKind == 1 && environment.ContainsKey("CSWEET_WORKLOAD_MEMORY_MB"))
            _cgroupPath = PrepareWorkloadCgroup(environment);
        var start = new ProcessStartInfo
        {
            FileName = _cgroupPath is not null ? "/bin/sh" : runAsUnprivilegedUser ? "/usr/bin/setpriv" : executable,
            WorkingDirectory = _artifactRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };
        if (_cgroupPath is not null)
        {
            // Fixed privileged trampoline: move before dropping privileges or executing any
            // artifact code. Entrypoint and arguments remain separate argv entries.
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("printf '%s' \"$$\" > \"$1\" || exit 126; shift; exec \"$@\"");
            start.ArgumentList.Add("csweet-workload-launch");
            start.ArgumentList.Add(Path.Combine(_cgroupPath, "cgroup.procs"));
            start.ArgumentList.Add("/usr/bin/setpriv");
        }
        if (runAsUnprivilegedUser)
        {
            start.ArgumentList.Add("--reuid=" + GuestUnixFilePermissions.WorkloadUser);
            start.ArgumentList.Add("--regid=" + GuestUnixFilePermissions.WorkloadUser);
            start.ArgumentList.Add("--init-groups");
            start.ArgumentList.Add("--no-new-privs");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(executable);
        }
        foreach (var argument in entrypoint.Skip(1))
        {
            if (argument.IndexOf('\0') >= 0 || argument.Length > 4096)
                throw new InvalidDataException("A workload argument is invalid.");
            start.ArgumentList.Add(argument);
        }
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["HOME"] = "/run/csweet/workload/home";
        start.Environment["CSWEET_RUNTIME_NETWORK_MODE"] = options.PublicInternet ? "public-internet" : "restricted";
        start.Environment["CSWEET_WORKLOAD_TOOLS"] = "/run/csweet/workload/tools";
        start.Environment["PATH"] = "/run/csweet/workload/tools/bin:" + start.Environment["PATH"];
        start.Environment["CSWEET_BROKER_ONLY"] = "1";
        start.Environment["CSweet__Agent__McpEndpoint"] = "http://localhost/mcp";
        start.Environment["CSweet__Agent__McpUnixSocketPath"] = options.LocalBrokerSocketPath;
        // The builder validates and packages exactly one canonical manifest under this name.
        // Override stale agent appsettings so previously published packages cannot point the SDK
        // at the retired csweet-agent.json convention.
        start.Environment["CSweet__Agent__ManifestPath"] = "csweet-plugin.json";
        if (options.WorkloadKind is 1 or 2)
        {
            WriteWorkloadToken();
            start.Environment["CSweet__Agent__InstallationId"] = options.InstallationId!.Value.ToString("D");
            start.Environment["CSweet__Agent__BusinessId"] = options.BusinessId!;
            start.Environment["CSweet__Agent__RuntimeInstanceId"] = options.WorkloadId.ToString("D");
            start.Environment["CSweet__Agent__TickId"] = options.TickId!.Value.ToString("D");
            start.Environment["CSweet__Agent__WorkloadTokenFile"] = options.WorkloadTokenPath;
        }
        if (environment.Count > 32)
            throw new InvalidDataException("The workload environment contains too many entries.");
        foreach (var item in environment)
        {
            if (!IsAllowedEnvironmentKey(item.Key) || item.Value.Length > 4096 || item.Value.IndexOf('\0') >= 0)
                throw new InvalidDataException("The workload environment contains an invalid entry.");
            start.Environment[item.Key] = item.Value;
        }

        _process = Process.Start(start) ?? throw new InvalidOperationException("The guest workload did not start.");
        _logDrains =
        [
            DrainBoundedAsync(_process.StandardOutput.BaseStream, maximumLogBytes, cancellationToken),
            DrainBoundedAsync(_process.StandardError.BaseStream, maximumLogBytes, cancellationToken)
        ];
        return Task.CompletedTask;
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("The guest workload has not started.");
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(_logDrains).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        return process.ExitCode;
    }

    public async Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        var process = _process;
        if (_cgroupPath is not null) File.WriteAllText(Path.Combine(_cgroupPath, "cgroup.kill"), "1");
        if (process is null || process.HasExited) return;
        try { process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(gracePeriod <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : gracePeriod);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            await StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            _process.Dispose();
        }
        if (File.Exists(options.WorkloadTokenPath)) File.Delete(options.WorkloadTokenPath);
    }

    private string ResolveExecutable(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
            throw new InvalidDataException("The workload executable is invalid.");
        var candidate = Path.GetFullPath(Path.IsPathFullyQualified(value)
            ? value
            : Path.Combine(_artifactRoot, value));
        if (options.WorkloadKind == 2 && string.Equals(
                candidate,
                "/usr/lib/csweet/toolchain/CSweet.Office.ToolchainGuest",
                StringComparison.Ordinal))
        {
            if (!File.Exists(candidate)) throw new FileNotFoundException("The certified toolchain workspace runner is missing.", candidate);
            return candidate;
        }
        var relative = Path.GetRelativePath(_artifactRoot, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new InvalidDataException("The workload executable escapes the read-only artifact root.");
        if (!File.Exists(candidate)) throw new FileNotFoundException("The workload executable is missing.", candidate);
        return candidate;
    }

    private async Task DrainBoundedAsync(Stream input, long maximumLogBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            AppendDiagnostic(buffer.AsSpan(0, read));
            if (Interlocked.Add(ref _logBytes, read) <= maximumLogBytes) continue;
            var process = _process;
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            throw new InvalidDataException("The workload exceeded its bounded log volume.");
        }
    }

    private void AppendDiagnostic(ReadOnlySpan<byte> content)
    {
        var text = Encoding.UTF8.GetString(content);
        lock (_diagnosticLock)
        {
            _diagnosticTail.Append(text);
            const int maximumCharacters = 8 * 1024;
            if (_diagnosticTail.Length > maximumCharacters)
                _diagnosticTail.Remove(0, _diagnosticTail.Length - maximumCharacters);
        }
    }

    private void WriteWorkloadToken()
    {
        var directory = Path.GetDirectoryName(options.WorkloadTokenPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(options.WorkloadTokenPath, options.BootToken);
        if (!OperatingSystem.IsWindows())
            GuestUnixFilePermissions.GrantWorkloadGroupAsync(
                options.WorkloadTokenPath,
                UnixFileMode.UserRead | UnixFileMode.GroupRead,
                CancellationToken.None).GetAwaiter().GetResult();
    }

    internal static bool IsAllowedEnvironmentKey(string value) => value is
        "CSWEET_WORKLOAD_MEMORY_MB" or "CSWEET_WORKLOAD_PROCESS_LIMIT" or
        "CSWEET_BUILD_ID" or "CSWEET_BUILD_EXPECTED_REVISION" or "CSWEET_BUILD_RECIPE_KEY" or
        "CSWEET_BUILD_TARGET_KEY" or "CSWEET_BUILD_CONFIGURATION_JSON" or
        "CSWEET_BUILD_SOURCE_URL" or "CSWEET_BUILD_SOURCE_COMMIT" or
        "CSWEET_BUILD_INPUT_ROOT" or "CSWEET_BUILD_OUTPUT_ROOT" or
        "CSWEET_BUILD_MAXIMUM_SOURCE_BYTES" or "CSWEET_BUILD_MAXIMUM_OUTPUT_BYTES" or "CSWEET_CERTIFIED_IMAGE_DIGEST" or
        "CSWEET_ALLOWED_DEPENDENCY_REGISTRIES" or "CSWEET_CERTIFICATION_FIXTURE_RESOURCE" or
        "CSWEET_WORKSPACE_MAXIMUM_ARCHIVE_BYTES" or "CSWEET_WORKSPACE_MAXIMUM_EXPANDED_BYTES" or
        "CSWEET_WORKSPACE_MAXIMUM_FILE_COUNT" or "CSWEET_CERTIFICATION_HOST_ADDRESSES";

    internal static long WorkloadMemoryBudget(long physicalBytes, long authorizedBytes)
    {
        var total = Math.Min(physicalBytes, authorizedBytes);
        var reserve = Math.Max(256L * 1024 * 1024, total / 4);
        if (total - reserve < 256L * 1024 * 1024) throw new InvalidDataException("The VM has insufficient memory for workload and supervision.");
        return total - reserve;
    }

    private static string PrepareWorkloadCgroup(IReadOnlyDictionary<string, string> environment)
    {
        var relative = File.ReadLines("/proc/self/cgroup").Single(x => x.StartsWith("0::", StringComparison.Ordinal))[3..];
        if (!relative.StartsWith('/') || relative.Split('/').Any(x => x == "..")) throw new InvalidDataException("Invalid delegated cgroup path.");
        var root = Path.GetFullPath("/sys/fs/cgroup" + relative);
        var supervision = Path.Combine(root, "supervision");
        Directory.CreateDirectory(supervision);
        // Delegate=yes gives this service a private subtree. Keep the broker outside the
        // memory-limited workload subtree; workload users cannot write either controller.
        foreach (var pid in File.ReadAllLines(Path.Combine(root, "cgroup.procs")))
            File.WriteAllText(Path.Combine(supervision, "cgroup.procs"), pid);
        File.WriteAllText(Path.Combine(root, "cgroup.subtree_control"), "+memory +pids");
        var workload = Path.Combine(root, "workload"); Directory.CreateDirectory(workload);
        var physicalKb = ReadCounter("/proc/meminfo", "MemTotal:");
        if (!environment.TryGetValue("CSWEET_WORKLOAD_MEMORY_MB", out var memory) || !long.TryParse(memory, out var mb) || mb <= 0 ||
            !environment.TryGetValue("CSWEET_WORKLOAD_PROCESS_LIMIT", out var limit) || !int.TryParse(limit, out var processes) || processes <= 0)
            throw new InvalidDataException("The signed workload resource envelope is incomplete.");
        var authorizedBytes = checked(mb * 1024 * 1024);
        File.WriteAllText(Path.Combine(workload, "memory.max"), WorkloadMemoryBudget(physicalKb * 1024, authorizedBytes).ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(workload, "memory.swap.max"), "0");
        File.WriteAllText(Path.Combine(workload, "memory.oom.group"), "1");
        File.WriteAllText(Path.Combine(workload, "pids.max"), processes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return workload;
    }

    private static long ReadCounter(string path, string name)
    {
        var fields = File.ReadLines(path).Select(x => x.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).FirstOrDefault(x => x.Length >= 2 && x[0] == name);
        return fields is not null && long.TryParse(fields[1], out var value) ? value : 0;
    }
}
