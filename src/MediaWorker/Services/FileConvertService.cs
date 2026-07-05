using System.Diagnostics;

namespace MediaWorker.Services;

/// <summary>
/// HEIC → JPEG and video → single-frame JPEG conversion (ported from the old
/// <c>ConvertFileHandler</c>). Keeps the hardening: <c>heif-convert</c> loses metadata, so it is
/// followed by <c>exiftool -TagsFromFile</c>; <c>--Orientation</c> is excluded from the copy
/// because <c>heif-convert</c> already applied the rotation physically.
/// </summary>
public sealed class FileConvertService
{
    public async Task ConvertHeicAsync(string sourcePath, string destPath, CancellationToken ct)
    {
        await RunAsync("heif-convert", ["-q", "90", sourcePath, destPath], ct);
        // --Orientation excludes the Orientation tag from the copy: heif-convert already applied
        // the rotation physically, so copying Orientation back would double-rotate (upside-down).
        await RunAsync("exiftool",
            ["-TagsFromFile", sourcePath, "--Orientation", destPath, "-overwrite_original", "-q"], ct);
    }

    public async Task ExtractVideoFrameAsync(string sourcePath, string destPath, CancellationToken ct) =>
        await RunAsync("ffmpeg",
            ["-i", sourcePath, "-ss", "00:00:01", "-vframes", "1", "-q:v", "2", destPath, "-y"], ct);

    private static async Task RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"{exe} exited {proc.ExitCode}: {err}");
        }
    }
}
