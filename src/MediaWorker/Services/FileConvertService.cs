using System.Diagnostics;
using System.Globalization;

namespace MediaWorker.Services;

/// <summary>
/// HEIC → JPEG and video → single-frame JPEG conversion (ported from the old
/// <c>ConvertFileHandler</c>). Keeps the hardening: <c>heif-convert</c> loses metadata, so it is
/// followed by <c>exiftool -TagsFromFile</c>; <c>--Orientation</c> is excluded from the copy
/// because <c>heif-convert</c> already applied the rotation physically.
/// </summary>
public sealed class FileConvertService
{
    // Below this, seeking is not worth the risk of landing past the last frame — take frame 0.
    private const double MinSeekSeconds = 0.25;

    public async Task ConvertHeicAsync(string sourcePath, string destPath, CancellationToken ct)
    {
        await RunAsync("heif-convert", ["-q", "90", sourcePath, destPath], ct);
        // --Orientation excludes the Orientation tag from the copy: heif-convert already applied
        // the rotation physically, so copying Orientation back would double-rotate (upside-down).
        await RunAsync("exiftool",
            ["-TagsFromFile", sourcePath, "--Orientation", destPath, "-overwrite_original", "-q"], ct);
    }

    /// <summary>
    /// Extracts one representative frame. The seek is duration-relative: a hardcoded
    /// <c>-ss 00:00:01</c> lands past the last frame of a short clip, and ffmpeg then exits 0
    /// having written no file at all — which is how 116 good videos failed the prod import at the
    /// *thumbnail* step with a misleading "could not find /shared-temp/…" (docs/backlog.md
    /// bucket 1). Frame 0 is the fallback, and the output is verified rather than assumed.
    /// </summary>
    public async Task ExtractVideoFrameAsync(string sourcePath, string destPath, CancellationToken ct)
    {
        var seek = SeekSeconds(await ProbeDurationAsync(sourcePath, ct));

        if (seek > 0)
        {
            try
            {
                await RunAsync("ffmpeg", BuildFrameArgs(sourcePath, destPath, seek), ct);
                if (HasOutput(destPath)) return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Swallowed on purpose: a seek that finds no frame is not a broken file. How it
                // surfaces depends on the ffmpeg build — 6.x exits 0 having written nothing (the
                // prod behaviour that produced the misleading /shared-temp error), 7.x/8.x fail
                // the encoder open instead. Both mean the same thing, and both retry below. If
                // the file really is unreadable, the frame-0 attempt raises the honest error.
            }
            // Frame 0 always exists, whatever the duration or frame timing says.
        }

        await RunAsync("ffmpeg", BuildFrameArgs(sourcePath, destPath, seek: 0), ct);

        if (!HasOutput(destPath))
            throw new InvalidOperationException(
                $"ffmpeg exited 0 but wrote no frame for {sourcePath}");
    }

    private static string[] BuildFrameArgs(string sourcePath, string destPath, double seek)
    {
        // -hide_banner -loglevel error: without them RunAsync's exception carries ffmpeg's version
        // banner and full ./configure line — ~2 KB of noise that pushes the actual reason off the
        // end of the Admin table's error column.
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-i", sourcePath };
        if (seek > 0)
        {
            args.Add("-ss");
            args.Add(seek.ToString("0.###", CultureInfo.InvariantCulture));
        }
        args.AddRange(["-vframes", "1", "-q:v", "2", destPath, "-y"]);
        return [.. args];
    }

    /// <summary>
    /// One second in, but never past the midpoint of the clip. Returns 0 (no seek — take frame 0)
    /// for unknown, zero, or very short durations.
    /// </summary>
    internal static double SeekSeconds(double? durationSec)
    {
        if (durationSec is not > 0) return 0;
        var seek = Math.Min(1.0, durationSec.Value / 2.0);
        return seek < MinSeekSeconds ? 0 : seek;
    }

    /// <summary>
    /// Container duration in seconds, or null if ffprobe cannot determine it. A failed probe is
    /// not a failed conversion — it just means the frame is taken from the start.
    /// </summary>
    private static async Task<double?> ProbeDurationAsync(string sourcePath, CancellationToken ct)
    {
        try
        {
            var stdout = await RunAsync("ffprobe",
                ["-v", "error", "-show_entries", "format=duration",
                 "-of", "default=noprint_wrappers=1:nokey=1", sourcePath], ct);

            return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool HasOutput(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static async Task<string> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;

        // Drain both pipes *concurrently* with the wait. Reading only after WaitForExitAsync
        // deadlocks any child that emits more than the ~64 KB pipe buffer: it blocks on write,
        // never exits, and takes the consumer down with it.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{exe} exited {proc.ExitCode}: {stderr}");

        return stdout;
    }
}
