using System.Diagnostics;
using System.Globalization;
using MediaWorker.Services;
using Xunit.Abstractions;

namespace MediaWorker.Tests.Services;

/// <summary>
/// Phase 12 Step 1 — the sub-second video regression. 116 good videos failed the prod archive
/// import because <c>ExtractVideoFrameAsync</c> hardcoded <c>-ss 00:00:01</c>, which seeks past
/// the last frame of a short clip (docs/backlog.md bucket 1).
/// <para>
/// The <see cref="FileConvertService.SeekSeconds"/> tests are pure logic and always run. The rest
/// drive the real binary against real clips; they need ffmpeg/ffprobe on PATH, which every
/// environment that runs this code has (the media-worker image installs both).
/// </para>
/// </summary>
public sealed class FileConvertServiceTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"hmh-convert-{Guid.NewGuid()}");

    private readonly ITestOutputHelper _output;

    public FileConvertServiceTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ── Seek selection (no ffmpeg needed) ────────────────────────────────────

    [Theory]
    [InlineData(null, 0)]        // ffprobe could not read a duration
    [InlineData(0.0, 0)]         // zero-length container
    [InlineData(0.067, 0)]       // shortest clip in the failing prod set
    [InlineData(0.3, 0)]         // midpoint 0.15 s is below the seek floor
    [InlineData(0.5, 0.25)]      // midpoint exactly at the floor
    [InlineData(1.0, 0.5)]
    [InlineData(1.113, 0.5565)]  // longest clip in the failing prod set — still under 1 s of seek
    [InlineData(2.0, 1.0)]       // from here on the seek is the full second
    [InlineData(600.0, 1.0)]
    public void SeekSeconds_IsDurationRelative_AndNeverPastTheMidpoint(double? duration, double expected)
    {
        Assert.Equal(expected, FileConvertService.SeekSeconds(duration), precision: 6);
    }

    [Fact]
    public void SeekSeconds_NegativeDuration_TreatedAsUnknown()
    {
        Assert.Equal(0, FileConvertService.SeekSeconds(-1));
    }

    // ── Real conversions ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.3)]    // the bug: no frame exists at 1 s
    [InlineData(1.05)]   // four of the 116 were over 1 s and failed anyway
    [InlineData(3.0)]    // the ordinary path, where the 1 s seek is used
    public async Task ExtractVideoFrame_ProducesAFrame_WhateverTheDuration(double durationSec)
    {
        if (!FfmpegAvailable()) return;

        var source = CreateClip($"clip-{durationSec}.mp4", durationSec);
        var dest = Path.Combine(_tempDir, $"frame-{durationSec}.jpg");

        await new FileConvertService().ExtractVideoFrameAsync(source, dest, CancellationToken.None);

        Assert.True(File.Exists(dest), $"no frame written for a {durationSec}s clip");
        Assert.True(new FileInfo(dest).Length > 0, "frame file is empty");
    }

    [Fact]
    public async Task ExtractVideoFrame_UnreadableFile_ThrowsWithoutTheFfmpegBanner()
    {
        if (!FfmpegAvailable()) return;

        // Truncated MOV with no moov atom — the shape of the 17 genuinely dead files.
        var source = Path.Combine(_tempDir, "broken.mov");
        await File.WriteAllBytesAsync(source, new byte[] { 0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70 });
        var dest = Path.Combine(_tempDir, "broken.jpg");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FileConvertService().ExtractVideoFrameAsync(source, dest, CancellationToken.None));

        // -hide_banner -loglevel error: the message must be the reason, not 2 KB of ./configure
        // flags that push it off the end of the Admin table's error column.
        Assert.DoesNotContain("--enable-", ex.Message);
        Assert.DoesNotContain("ffmpeg version", ex.Message);
        Assert.True(ex.Message.Length < 1000, $"error message is {ex.Message.Length} chars: {ex.Message}");
    }

    [Fact]
    public async Task ExtractVideoFrame_MissingFile_Throws()
    {
        if (!FfmpegAvailable()) return;

        var dest = Path.Combine(_tempDir, "nothing.jpg");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FileConvertService().ExtractVideoFrameAsync(
                Path.Combine(_tempDir, "does-not-exist.mp4"), dest, CancellationToken.None));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string CreateClip(string name, double durationSec)
    {
        var path = Path.Combine(_tempDir, name);
        var duration = durationSec.ToString("0.###", CultureInfo.InvariantCulture);

        Run("ffmpeg",
            ["-hide_banner", "-loglevel", "error",
             "-f", "lavfi", "-i", $"testsrc=duration={duration}:size=320x240:rate=30",
             "-c:v", "libx264", "-pix_fmt", "yuv420p", path, "-y"]);

        return path;
    }

    private bool FfmpegAvailable()
    {
        try
        {
            Run("ffmpeg", ["-hide_banner", "-version"]);
            Run("ffprobe", ["-hide_banner", "-version"]);
            return true;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"SKIPPED — ffmpeg/ffprobe not on PATH: {ex.Message}");
            return false;
        }
    }

    private static void Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{exe} exited {proc.ExitCode}: {stderr.Result}");
    }
}
