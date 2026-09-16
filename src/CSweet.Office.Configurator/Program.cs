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
if (!IsAdministrator())
{
    try
    {
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

var fragment = input.Fragment.TrimStart('#');
if (!fragment.StartsWith("handoff=", StringComparison.Ordinal)) return 2;
var handoff = Uri.UnescapeDataString(fragment["handoff=".Length..]);
var query = Arguments.Query(input);
if (!query.TryGetValue("session", out var sessionText) || !Guid.TryParse(sessionText, out var sessionId) ||
    !query.TryGetValue("origin", out var originText) ||
    !Uri.TryCreate(originText, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps)
    return 2;
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
var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
var existingState = ProbeExistingInstallation(installRoot, query.ContainsKey("office") && query.GetValueOrDefault("operation") == "upgrade");
var targeted = query.TryGetValue("office", out var targetText);
var maintenance = targeted && query.GetValueOrDefault("operation") is "repair" or "upgrade";
if (targeted && (!Guid.TryParse(targetText, out _) ||
    !query.TryGetValue("operation", out var operation) || operation is not ("repair" or "upgrade" or "remove"))) return 2;


if (targeted)
{
    var statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CSweet", "Office", "node", "node-state.json");
    using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
    if (identity.RootElement.GetProperty("OfficeId").GetGuid() != Guid.Parse(targetText!)) return 4;
}

using var preflightResponse = await client.PostAsJsonAsync("api/offices/local-sessions/preflight",
    new AssistedOfficePreflightRequest(handoff, Environment.MachineName, "windows", architecture, version,
        existingState));
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
        var statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CSweet", "Office", "node", "node-state.json");
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
        if (identity.RootElement.GetProperty("OfficeId").GetGuid() != Guid.Parse(targetText!)) return 4;
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
            uri.Scheme == "csweet-office" && uri.Host == "enroll" && uri.AbsolutePath == "/v1" ? uri : null;
    }

    public static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
        .Where(part => part.Length == 2)
        .ToDictionary(part => System.Uri.UnescapeDataString(part[0]), part => System.Uri.UnescapeDataString(part[1]),
            StringComparer.OrdinalIgnoreCase);
}
