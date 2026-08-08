using System.Collections.Concurrent;
using System.Diagnostics;
using Api.Data;
using Api.Infrastructure;
using Data.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.StreamMedia;

public record StreamMediaRequest(Guid Id, string VideoCacheDir) : IRequest<StreamMediaResult?>;

public record StreamMediaResult(string FilePath, string ContentType);

public sealed class StreamMediaHandler : IRequestHandler<StreamMediaRequest, StreamMediaResult?>
{
    // One transcode per media file at a time. Without it, every click on a slow video starts
    // another ffmpeg writing the same output. Entries are never removed: one SemaphoreSlim per
    // video actually streamed is negligible, and removing them introduces a race where two
    // requests pick up different gates and defeat the point.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> InFlight = new();

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly AppDbContext _db;
    private readonly ILogger<StreamMediaHandler> _logger;
    private readonly TimeSpan _transcodeTimeout;

    public StreamMediaHandler(AppDbContext db, ILogger<StreamMediaHandler> logger, IConfiguration config)
    {
        _db     = db;
        _logger = logger;
        _transcodeTimeout = TimeSpan.FromMinutes(config.GetValue("Video:TranscodeTimeoutMinutes", 30.0));
    }

    public async Task<StreamMediaResult?> Handle(StreamMediaRequest req, CancellationToken ct)
    {
        var f = await _db.MediaFiles
            .Where(x => x.Id == req.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (f is null || !f.DurationSec.HasValue || !File.Exists(f.FilePath))
            return null;

        var cached = await _db.VideoTranscodes
            .Where(v => v.MediaFileId == req.Id)
            .FirstOrDefaultAsync(ct);

        if (cached is not null && File.Exists(cached.TranscodePath))
            return new StreamMediaResult(cached.TranscodePath, "video/mp4");

        var outputPath = Path.Combine(req.VideoCacheDir, $"{req.Id}.mp4");

        var gate = InFlight.GetOrAdd(req.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another request may have finished it while we queued.
            if (File.Exists(outputPath))
                return new StreamMediaResult(outputPath, "video/mp4");

            return await TranscodeAsync(req.Id, f.FilePath, req.VideoCacheDir, outputPath, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<StreamMediaResult?> TranscodeAsync(
        Guid id, string sourcePath, string cacheDir, string outputPath, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDir);

        var codecs = await ProbeCodecsAsync(sourcePath);

        // Remux rather than re-encode when the container already holds browser-playable streams.
        // An iPhone .MOV is usually H.264/AAC in a QuickTime wrapper, so this is a stream copy —
        // seconds, against the minutes a 155 MB libx264 pass costs on this CPU.
        var remuxing = codecs.Video is "h264";
        string[] videoArgs = remuxing
            ? ["-c:v", "copy"]
            : ["-c:v", "libx264", "-preset", "fast", "-crf", "23"];

        string[] audioArgs = codecs.Audio is "aac"                 ? ["-c:a", "copy"]
                           : codecs.Probed && codecs.Audio is null ? ["-an"]
                           : ["-c:a", "aac"];

        // Write to a unique temp file and move on success. The response streams straight off
        // disk, so a half-written .mp4 would otherwise be served — and cached — as complete.
        var tempPath = Path.Combine(cacheDir, $"{id}.{Guid.NewGuid():N}.part.mp4");

        var psi = new ProcessStartInfo("ffmpeg");
        // -nostdin: ffmpeg reads stdin by default and can block waiting on it.
        // -loglevel error: keeps stderr small as well as drained.
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
        foreach (var a in videoArgs) psi.ArgumentList.Add(a);
        foreach (var a in audioArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(tempPath);

        var started = Stopwatch.StartNew();
        var result  = await ProcessRunner.RunAsync(psi, _transcodeTimeout);

        if (!result.Succeeded || !File.Exists(tempPath))
        {
            _logger.LogError(
                "ffmpeg {Mode} failed for {MediaId} (timedOut={TimedOut}, exit={ExitCode}): {Error}",
                remuxing ? "remux" : "re-encode", id, result.TimedOut, result.ExitCode, result.StandardError);
            TryDelete(tempPath);
            return null;
        }

        File.Move(tempPath, outputPath, overwrite: true);
        _logger.LogInformation("ffmpeg {Mode} for {MediaId} took {Elapsed}",
            remuxing ? "remux" : "re-encode", id, started.Elapsed);

        var info  = new FileInfo(outputPath);
        var entry = new VideoTranscode
        {
            MediaFileId   = id,
            TranscodePath = outputPath,
            Codec         = "h264",
            Container     = "mp4",
            FileSize      = info.Length,
        };

        try
        {
            _db.VideoTranscodes.Add(entry);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Duplicate insert from a concurrent request is harmless — the file is on disk.
            _logger.LogWarning(ex, "VideoTranscode insert skipped for {MediaId}", id);
        }

        return new StreamMediaResult(outputPath, "video/mp4");
    }

    private sealed record Codecs(string? Video, string? Audio, bool Probed);

    /// <summary>
    /// One probe per stream type. A single combined call is tempting but wrong: with
    /// <c>-show_entries stream=codec_type,codec_name</c> ffprobe emits the fields in *its own*
    /// internal order (codec_name first), not the order requested, so a positional CSV parse
    /// silently mismatches — which stripped the audio from every video and missed the remux path.
    /// Selecting one stream and asking for one field cannot be misread.
    /// </summary>
    private async Task<Codecs> ProbeCodecsAsync(string sourcePath)
    {
        var video = await ProbeStreamCodecAsync(sourcePath, "v:0");
        if (video is null)
        {
            // No video stream, or ffprobe failed — fall back to a full re-encode, which is
            // always correct if slow. Audio is left unknown so it is re-encoded rather than dropped.
            _logger.LogWarning("ffprobe found no video stream for {Path}", sourcePath);
            return new Codecs(null, null, Probed: false);
        }

        return new Codecs(video, await ProbeStreamCodecAsync(sourcePath, "a:0"), Probed: true);
    }

    private async Task<string?> ProbeStreamCodecAsync(string sourcePath, string stream)
    {
        var psi = new ProcessStartInfo("ffprobe");
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add(stream);
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("stream=codec_name");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=nw=1:nk=1");
        psi.ArgumentList.Add(sourcePath);

        var probe = await ProcessRunner.RunAsync(psi, ProbeTimeout);
        if (!probe.Succeeded)
        {
            _logger.LogWarning("ffprobe {Stream} failed for {Path}: {Error}",
                stream, sourcePath, probe.StandardError);
            return null;
        }

        // Empty output means the stream simply is not there (common: a video with no audio).
        var codec = probe.StandardOutput.Trim();
        return codec.Length == 0 ? null : codec;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
