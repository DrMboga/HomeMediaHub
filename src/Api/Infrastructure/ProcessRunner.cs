using System.Diagnostics;

namespace Api.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs a child process to completion without the two ways this goes wrong.
///
/// <para><b>Pipes must be drained while the process runs.</b> The OS pipe buffer is only tens of
/// KB; a chatty producer blocks forever on write once it fills. This is what made large videos
/// hang indefinitely while short clips transcoded fine — the clip finished before ffmpeg had
/// written enough stderr to fill the buffer.</para>
///
/// <para><b>Disposing a Process does not kill it.</b> Without an explicit
/// <c>Kill(entireProcessTree: true)</c>, an abandoned run leaves the child alive and retries
/// stack more of them onto the same output. Mirrors the ExifTool rule in CLAUDE.md.</para>
///
/// <para>Deliberately takes no <see cref="CancellationToken"/>: these runs populate a cache, so a
/// browser that gives up early should not throw away work another request is about to want. The
/// timeout is the only bound.</para>
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(ProcessStartInfo psi, TimeSpan timeout)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.UseShellExecute        = false;
        psi.CreateNoWindow         = true;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {psi.FileName}");

        // Start draining before awaiting exit — the ordering is the whole point.
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            TryKill(proc);
        }

        // Killing closes the pipes, so both drains complete on either path.
        var outText = await Drain(stdout);
        var errText = await Drain(stderr);

        return new ProcessResult(timedOut ? -1 : proc.ExitCode, outText, errText, timedOut);
    }

    private static async Task<string> Drain(Task<string> read)
    {
        try { return await read; }
        catch { return ""; }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // Raced with normal exit — nothing to kill.
        }
    }
}
