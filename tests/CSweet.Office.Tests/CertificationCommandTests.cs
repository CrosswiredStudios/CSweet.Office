using System.Diagnostics;

namespace CSweet.Office.Tests;

public sealed class CertificationCommandTests
{
    [Fact]
    public async Task TimeoutRetainsOutputWrittenBeforeTheCommandWasKilled()
    {
        var result = await CertificationCommand.RunAsync(Command(
            "[Console]::Out.WriteLine('stdout-before-timeout'); [Console]::Error.WriteLine('registry-request-stalled'); Start-Sleep -Seconds 30",
            "echo stdout-before-timeout; echo registry-request-stalled >&2; sleep 30"), TimeSpan.FromSeconds(3));
        Assert.True(result.TimedOut);
        Assert.Contains("stdout-before-timeout", result.Output);
        Assert.Contains("registry-request-stalled", result.Error);
    }

    [Fact]
    public async Task OutputTailsAreBoundedAndNonzeroExitIsPreserved()
    {
        var result = await CertificationCommand.RunAsync(Command(
            "[Console]::Out.Write(('x' * 12000) + 'output-tail'); [Console]::Error.Write(('y' * 12000) + 'error-tail'); exit 2",
            "head -c 12000 /dev/zero | tr '\\0' x; printf output-tail; (head -c 12000 /dev/zero | tr '\\0' y; printf error-tail) >&2; exit 2"), TimeSpan.FromSeconds(10));
        Assert.False(result.TimedOut);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(4096, result.Output.Length);
        Assert.Equal(4096, result.Error.Length);
        Assert.EndsWith("output-tail", result.Output);
        Assert.EndsWith("error-tail", result.Error);
    }

    private static ProcessStartInfo Command(string windows, string linux)
    {
        var command = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            command.ArgumentList.Add("-NoProfile"); command.ArgumentList.Add("-NonInteractive");
            command.ArgumentList.Add("-Command"); command.ArgumentList.Add(windows);
        }
        else { command.ArgumentList.Add("-c"); command.ArgumentList.Add(linux); }
        return command;
    }
}
