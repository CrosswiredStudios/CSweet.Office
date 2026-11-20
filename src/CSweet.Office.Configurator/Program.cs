using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using CSweet.Office.Contracts.ControlPlane;

if (!OperatingSystem.IsWindows()) return 2;
if (args.Contains("--maintenance-service", StringComparer.Ordinal))
{
    await OfficeMaintenanceService.RunAsync(args);
    return 0;
}
var input = Arguments.Uri(args);
if (input is null) return 2;
if (!Arguments.TryReadHandoff(input, out var query, out var origin, out var sessionId, out var handoff)) return 2;
if (!IsAdministrator())
{
    try
    {
        // A saved browser link cannot start privileged repair or upgrade. Enrolled
        // Offices receive those requests through the authenticated Maintenance claim.
        if (!Arguments.AllowInteractiveLaunch(query, HasInstalledOffice)) return 4;
        var elevated = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        elevated.ArgumentList.Add("--uri");
        elevated.ArgumentList.Add(input.AbsoluteUri);
        using var process = Process.Start(elevated);
        if (process is null) return 3;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
    catch { return 3; }
}

query.TryGetValue("certificate", out var expectedCertificate);

var handler = new HttpClientHandler { AllowAutoRedirect = false };
if (!string.IsNullOrWhiteSpace(expectedCertificate))
{
    var expected = Convert.FromHexString(NormalizeHex(expectedCertificate));
    handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
        certificate is not null && CryptographicOperations.FixedTimeEquals(
            expected, SHA256.HashData(certificate.Export(X509ContentType.Cert)));
}
using var client = new HttpClient(handler) { BaseAddress = origin };
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.3.0";
var installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
if (string.Equals(Path.GetFileName(installRoot), "recovery", StringComparison.OrdinalIgnoreCase))
    installRoot = Path.GetDirectoryName(installRoot)!;
var installedStorage = ReadInstalledStorage(installRoot);
var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
var existingState = ProbeExistingInstallation(installRoot, query.ContainsKey("office") && query.GetValueOrDefault("operation") == "upgrade");
var targeted = query.TryGetValue("office", out var targetText);
var maintenance = targeted && query.GetValueOrDefault("operation") is "repair" or "upgrade";
if (targeted && (!Guid.TryParse(targetText, out _) ||
    !query.TryGetValue("operation", out var operation) || operation is not ("repair" or "upgrade" or "remove"))) return 2;
if (maintenance && query.GetValueOrDefault("operation") == "upgrade" && query.GetValueOrDefault("automatic") == "true")
{
    // This helper is installed with the independently released Office, never supplied as
    // executable code by Headquarters. It accepts only the existing machine-bound handoff.
    var helper = Path.Combine(installRoot, "Start-CSweetOfficeAutomaticUpdate.ps1");
    if (!File.Exists(helper)) return 5;
    var handoffPath = Path.Combine(Path.GetTempPath(), $"csweet-update-{Guid.NewGuid():N}.secret");
    try
    {
        await File.WriteAllTextAsync(handoffPath, input.AbsoluteUri);
        if (Run("icacls.exe", [handoffPath, "/inheritance:r", "/grant:r", "*S-1-5-18:F", "*S-1-5-32-544:F"]) != 0) return 5;
        return Run(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", helper, "-HandoffInputPath", handoffPath]);
    }
    finally { try { File.Delete(handoffPath); } catch (IOException) { } }
}


if (targeted)
{
    if (!await MatchesInstalledOffice(installedStorage, Guid.Parse(targetText!))) return 4;
}

using var preflightResponse = await client.PostAsJsonAsync("api/offices/local-sessions/preflight",
    new AssistedOfficePreflightRequest(handoff, Environment.MachineName, "windows", architecture, version,
        existingState, installedStorage));
var preflight = await preflightResponse.Content.ReadFromJsonAsync<AssistedOfficePreflightResponse>();
if (preflight is null || preflight.AssistedSetupSessionId != sessionId) return 4;
if (string.Equals(preflight.ExistingInstallationAction, "remove", StringComparison.Ordinal))
{
    if (string.IsNullOrWhiteSpace(preflight.SetupReceipt)) return 4;
    if (StageRemoval(installRoot, origin, expectedCertificate, handoff, sessionId, preflight.SetupReceipt)) return 0;
    using var removalFailure = await client.PostAsJsonAsync("api/offices/local-sessions/result",
        new ReportAssistedOfficeSetupResultRequest(sessionId, preflight.SetupReceipt, "office_removal_failed",
            Environment.MachineName, "windows", architecture));
    return 6;
}
if (!preflight.ProceedToRedemption) return 0;
if (!preflightResponse.IsSuccessStatusCode || !preflight.Succeeded) return 4;
if (preflight.Storage is { } requestedStorage && !ValidateStorage(installRoot, requestedStorage)) return 5;

using var response = await client.PostAsJsonAsync("api/offices/local-sessions/redeem",
    new RedeemAssistedOfficeSetupRequest(handoff, Environment.MachineName, "windows", architecture, version));
var redemption = await response.Content.ReadFromJsonAsync<RedeemAssistedOfficeSetupResponse>();
if (!response.IsSuccessStatusCode || redemption is not { Succeeded: true } || (!maintenance && string.IsNullOrWhiteSpace(redemption.EnrollmentToken)) ||
    redemption.AssistedSetupSessionId != sessionId || string.IsNullOrWhiteSpace(redemption.SetupReceipt) ||
    !Uri.TryCreate(redemption.ControlPlaneUrl, UriKind.Absolute, out var controlPlane) ||
    controlPlane.Scheme != Uri.UriSchemeHttps)
    return 4;

var tokenPath = Path.Combine(Path.GetTempPath(), $"csweet-enrollment-{Guid.NewGuid():N}.secret");
var progressPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "CSweet", "Setup", $"windows-isolation-{sessionId:N}.json");
try
{
    if (maintenance)
    {
        if (redemption.ExistingInstallationAction != query["operation"]) return 4;
        if (!await MatchesInstalledOffice(installedStorage, Guid.Parse(targetText!))) return 4;
        var preparation = Path.Combine(installRoot, "Enter-CSweetOfficeMaintenance.ps1");
        if (!File.Exists(preparation)) throw new InvalidDataException("The recovery app needs to be updated.");
        if (Run(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", preparation, "-OfficeId", targetText!]) != 0)
        {
            await client.PostAsJsonAsync("api/offices/local-sessions/result",
                new ReportAssistedOfficeSetupResultRequest(sessionId, redemption.SetupReceipt, "office_setup_failed",
                    Environment.MachineName, "windows", architecture));
            return 5;
        }
    }
    else
    {
        await File.WriteAllTextAsync(tokenPath, redemption.EnrollmentToken);
        if (Run("icacls.exe", [tokenPath, "/inheritance:r", "/grant:r", "*S-1-5-18:F", "*S-1-5-32-544:F"]) != 0) return 5;
    }
    var script = Path.Combine(installRoot, "Install-CSweetOffice.ps1");
    var payload = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    if (!File.Exists(script) || !Directory.Exists(payload)) throw new InvalidDataException("The recovery payload is missing.");
    var arguments = new List<string>
    {
        "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
        "-PayloadRoot", payload, "-ControlPlaneUrl", controlPlane.AbsoluteUri,
        "-AssistedSetupSessionId", sessionId.ToString("D"),
        "-AllocatableCpuCount", redemption.AllocatableCpuCount.ToString(),
        "-AllocatableMemoryMb", redemption.AllocatableMemoryMb.ToString(),
        "-AllocatableDiskMb", redemption.AllocatableDiskMb.ToString(),
        "-MaximumConcurrentWorkloads", redemption.MaximumConcurrentWorkloads.ToString(),
        "-ExistingInstallationAction", maintenance ? "upgrade" : redemption.ExistingInstallationAction,
        "-ProgressPath", progressPath, "-ProgressJobId", sessionId.ToString("D"),
        "-NonInteractive", "-Elevated"
    };
    if (redemption.Storage is { } storage)
        arguments.AddRange(["-StorageVolumeId", storage.VolumeId, "-InstallRoot", storage.InstallRoot, "-DataRoot", storage.DataRoot]);
    if (!maintenance) arguments.AddRange(["-EnrollmentTokenInputPath", tokenPath]);
    if (!string.IsNullOrWhiteSpace(redemption.ControlPlaneCertificateSha256))
    {
        arguments.Add("-ControlPlaneCertificateSha256");
        arguments.Add(redemption.ControlPlaneCertificateSha256);
    }
    var exitCode = Run(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), arguments);
    if (exitCode != 0)
    {
        var errorCode = ReadSetupErrorCode(progressPath);
        using var result = await client.PostAsJsonAsync("api/offices/local-sessions/result",
            new ReportAssistedOfficeSetupResultRequest(sessionId, redemption.SetupReceipt,
                errorCode is "existing_office_detected" or "existing_office_active" or "reconnect_unsafe"
                    ? errorCode : "office_setup_failed", Environment.MachineName, "windows", architecture));
    }
    if (maintenance && exitCode == 0)
        await client.PostAsJsonAsync("api/offices/local-sessions/result",
            new ReportAssistedOfficeSetupResultRequest(sessionId, redemption.SetupReceipt, "office_upgrade_completed",
                Environment.MachineName, "windows", architecture));
    return exitCode;
}
catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
{
    try
    {
        using var failure = await client.PostAsJsonAsync("api/offices/local-sessions/result",
            new ReportAssistedOfficeSetupResultRequest(sessionId, redemption.SetupReceipt, "office_setup_failed",
                Environment.MachineName, "windows", architecture));
    }
    catch (HttpRequestException) { }
    return 5;
}
finally
{
    try { File.Delete(tokenPath); } catch { }
}

static async Task<bool> MatchesInstalledOffice(OfficeInstallationLocation? storage, Guid officeId)
{
    var root = storage?.DataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CSweet", "Office");
    using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "node", "node-state.json")));
    return identity.RootElement.GetProperty("OfficeId").GetGuid() == officeId;
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static bool HasInstalledOffice()
{
    using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services")
        ?? throw new IOException("Windows service registration is unavailable.");
    foreach (var name in new[] { "CSweet.Office.Node", "CSweet.Office.RuntimeHost", "CSweet.Office.Maintenance" })
    {
        using var service = services.OpenSubKey(name);
        if (service is not null) return true;
    }
    return false;
}

static OfficeInstallationLocation? ReadInstalledStorage(string installRoot)
{
    var helper = Path.Combine(installRoot, "CSweet.OfficeStorage.ps1");
    if (!File.Exists(helper)) return null;
    var script = "$ErrorActionPreference='Stop'; . '" + helper.Replace("'", "''") + "'; ConvertTo-Json -InputObject (Get-CSweetInstalledOfficeStorage) -Compress";
    var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Office storage discovery could not start.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(15_000)) { process.Kill(entireProcessTree: true); throw new IOException("Office storage discovery timed out."); }
    if (process.ExitCode != 0) throw new IOException("Office storage discovery failed.");
    _ = error.GetAwaiter().GetResult();
    return JsonSerializer.Deserialize<OfficeInstallationLocation>(output.GetAwaiter().GetResult());
}

static bool ValidateStorage(string installRoot, OfficeInstallationLocation storage)
{
    var helper = Path.Combine(installRoot, "CSweet.OfficeStorage.ps1");
    if (!File.Exists(helper)) return false;
    var data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(storage)));
    var script = "$ErrorActionPreference='Stop'; . '" + helper.Replace("'", "''") + "'; " +
        "$s=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json; " +
        "Resolve-CSweetOfficeStorage -VolumeId $s.VolumeId -InstallRoot $s.InstallRoot -DataRoot $s.DataRoot -RequiredDiskMb $s.RequiredDiskMb | Out-Null";
    return Run(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))]) == 0;
}

static string ProbeExistingInstallation(string installRoot, bool forUpgrade)
{
    var script = Path.Combine(installRoot, "Get-CSweetOfficeRecoveryState.ps1");
    if (!File.Exists(script)) return "unsafe";
    var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
        start.ArgumentList.Add(argument);
    if (forUpgrade) start.ArgumentList.Add("-ForUpgrade");
    using var process = Process.Start(start);
    if (process is null) return "unsafe";
    var output = process.StandardOutput.ReadToEnd().Trim().ToLowerInvariant();
    process.WaitForExit();
    return process.ExitCode == 0 && output is "none" or "clean" or "active" or "unsafe" ? output : "unsafe";
}

static bool StageRemoval(string installRoot, Uri origin, string? certificate, string handoff, Guid sessionId,
    string setupReceipt)
{
    try
    {
        var stageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CSweet", "Setup", $"remove-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageRoot);
        var helper = Path.Combine(stageRoot, "Remove-CSweetOfficeForRecovery.ps1");
        var uninstaller = Path.Combine(stageRoot, "Uninstall-CSweetOffice.ps1");
        var secret = Path.Combine(stageRoot, "handoff.secret");
        var receipt = Path.Combine(stageRoot, "setup-receipt.secret");
        File.Copy(Path.Combine(installRoot, "Remove-CSweetOfficeForRecovery.ps1"), helper, overwrite: false);
        File.Copy(Path.Combine(installRoot, "Uninstall-CSweetOffice.ps1"), uninstaller, overwrite: false);
        File.Copy(Path.Combine(installRoot, "CSweet.OfficeStorage.ps1"), Path.Combine(stageRoot, "CSweet.OfficeStorage.ps1"), overwrite: false);
        File.WriteAllText(secret, handoff);
        File.WriteAllText(receipt, setupReceipt);
        if (Run("icacls.exe", [stageRoot, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F"]) != 0)
            return false;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper,
            "-ParentProcessId", Environment.ProcessId.ToString(), "-UninstallScript", uninstaller,
            "-ControlPlaneOrigin", origin.AbsoluteUri, "-HandoffSecretPath", secret,
            "-AssistedSetupSessionId", sessionId.ToString("D"), "-SetupReceiptPath", receipt,
            "-ControlPlaneCertificateSha256", certificate ?? ""
        }) start.ArgumentList.Add(argument);
        return Process.Start(start) is not null;
    }
    catch { return false; }
}

static string ReadSetupErrorCode(string path)
{
    try
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("errorCode", out var value) ? value.GetString() ?? "" : "";
    }
    catch { return ""; }
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

static int Run(string fileName, IReadOnlyList<string> arguments)
{
    var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start);
    if (process is null) return -1;
    process.WaitForExit();
    return process.ExitCode;
}

static string NormalizeHex(string value)
{
    var normalized = new string(value.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());
    if (normalized.Length != 64) throw new InvalidDataException("Certificate fingerprint is invalid.");
    return normalized;
}

static class Arguments
{
    public static Uri? Uri(string[] args)
    {
        var value = args.Length == 1 ? args[0] : args.Length == 2 && args[0] == "--uri" ? args[1] : null;
        return System.Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == "csweet-office" && uri.Host == "enroll" && uri.IsDefaultPort &&
            uri.UserInfo.Length == 0 && uri.AbsolutePath == "/v1" ? uri : null;
    }

    public static bool TryReadHandoff(Uri uri, out Dictionary<string, string> query, out Uri origin, out Guid sessionId, out string handoff)
    {
        query = new(StringComparer.OrdinalIgnoreCase);
        origin = null!;
        sessionId = default;
        handoff = "";
        if (!uri.Fragment.StartsWith("#handoff=", StringComparison.Ordinal) || uri.Fragment.Length is <= 9 or > 265) return false;
        handoff = uri.Fragment[9..];
        if (handoff.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") >= 0) return false;
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !query.TryAdd(System.Uri.UnescapeDataString(pair[0]), System.Uri.UnescapeDataString(pair[1]))) return false;
        }
        if (!Guid.TryParse(query.GetValueOrDefault("session"), out sessionId) || sessionId == Guid.Empty ||
            !System.Uri.TryCreate(query.GetValueOrDefault("origin"), UriKind.Absolute, out var parsed) ||
            parsed.Scheme != "https" || parsed.UserInfo.Length != 0 || parsed.Query.Length != 0 || parsed.Fragment.Length != 0) return false;
        origin = parsed;
        return true;
    }

    public static bool AllowInteractiveLaunch(Dictionary<string, string> query, Func<bool> installed)
    {
        if (query.ContainsKey("office") || query.ContainsKey("operation"))
            return query.GetValueOrDefault("operation") == "remove" && Guid.TryParse(query.GetValueOrDefault("office"), out var id) && id != Guid.Empty;
        return !query.ContainsKey("automatic") && !query.ContainsKey("localSource") && !installed();
    }
}
