using System.Diagnostics;
using System.Text;

internal sealed record CertificationCommandResult(int ExitCode, bool TimedOut, string Output, string Error);

internal static class CertificationCommand
{
    internal static async Task<CertificationCommandResult> RunAsync(ProcessStartInfo start, TimeSpan maximumDuration)
    {
        using var process = Process.Start(start) ?? throw new IOException("The certification command did not start.");
        // Drain independently of the command timeout so killing a timed-out command
        // retains its last output instead of cancelling and discarding both reads.
        var output = ReadTailAsync(process.StandardOutput);
        var error = ReadTailAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(maximumDuration);
        var timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
        }
        return new(process.ExitCode, timedOut, await output, await error);
    }

    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[2048];
        while (true)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0) return tail.ToString();
            tail.Append(buffer, 0, count);
            if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);
        }
    }
}
