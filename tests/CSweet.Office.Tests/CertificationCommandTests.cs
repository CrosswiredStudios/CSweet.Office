using System.Diagnostics;

namespace CSweet.Office.Tests;

public sealed class CertificationCommandTests
{
    [Fact]
    public async Task TimeoutRetainsOutputWrittenBeforeTheCommandWasKilled()
    {
        var result = await CertificationCommand.RunAsync(Command(
            "echo stdout-before-timeout& echo registry-request-stalled 1>&2& ping.exe -n 31 127.0.0.1 >nul",
            "echo stdout-before-timeout; echo registry-request-stalled >&2; sleep 30"), TimeSpan.FromSeconds(3));
        Assert.True(result.TimedOut);
        Assert.Contains("stdout-before-timeout", result.Output);
        Assert.Contains("registry-request-stalled", result.Error);
    }

    [Fact]
    public async Task OutputTailsAreBoundedAndNonzeroExitIsPreserved()
    {
        var result = await CertificationCommand.RunAsync(Command(
            "(for /L %i in (1,1,1200) do @echo xxxxxxxxxx)& echo output-tail& (for /L %i in (1,1,1200) do @echo yyyyyyyyyy) 1>&2& echo error-tail 1>&2& exit /b 2",
            "head -c 12000 /dev/zero | tr '\\0' x; printf output-tail; (head -c 12000 /dev/zero | tr '\\0' y; printf error-tail) >&2; exit 2"), TimeSpan.FromSeconds(10));
        Assert.False(result.TimedOut);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(4096, result.Output.Length);
        Assert.Equal(4096, result.Error.Length);
        Assert.EndsWith("output-tail", result.Output.TrimEnd());
        Assert.EndsWith("error-tail", result.Error.TrimEnd());
    }

    private static ProcessStartInfo Command(string windows, string linux)
    {
        // These tests exercise process supervision, not PowerShell startup/module discovery.
        var command = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/sh")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            command.ArgumentList.Add("/d"); command.ArgumentList.Add("/c"); command.ArgumentList.Add(windows);
        }
        else { command.ArgumentList.Add("-c"); command.ArgumentList.Add(linux); }
        return command;
    }
}
