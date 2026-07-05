using Api.Data;
using Data.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.StreamMedia;

public record StreamMediaRequest(Guid Id, string VideoCacheDir) : IRequest<StreamMediaResult?>;

public record StreamMediaResult(string FilePath, string ContentType);

public sealed class StreamMediaHandler : IRequestHandler<StreamMediaRequest, StreamMediaResult?>
{
    private readonly AppDbContext _db;
    private readonly ILogger<StreamMediaHandler> _logger;

    public StreamMediaHandler(AppDbContext db, ILogger<StreamMediaHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<StreamMediaResult?> Handle(StreamMediaRequest req, CancellationToken ct)
    {
        var f = await _db.MediaFiles
            .Where(x => x.Id == req.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (f is null || !f.DurationSec.HasValue)
            return null;

        // Return cached transcode if available
        var cached = await _db.VideoTranscodes
            .Where(v => v.MediaFileId == req.Id)
            .FirstOrDefaultAsync(ct);

        if (cached is not null && File.Exists(cached.TranscodePath))
            return new StreamMediaResult(cached.TranscodePath, "video/mp4");

        // Transcode on demand
        var outputPath = Path.Combine(req.VideoCacheDir, $"{req.Id}.mp4");
        Directory.CreateDirectory(req.VideoCacheDir);

        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            Arguments              = $"-i \"{f.FilePath}\" -c:v libx264 -preset fast -crf 23 -c:a aac -movflags +faststart -y \"{outputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0 || !File.Exists(outputPath))
        {
            _logger.LogError("ffmpeg transcode failed for {MediaId}", req.Id);
            return null;
        }

        var info  = new FileInfo(outputPath);
        var entry = new VideoTranscode
        {
            MediaFileId   = req.Id,
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
            // Duplicate insert from a concurrent request is harmless
            _logger.LogWarning(ex, "VideoTranscode insert skipped for {MediaId}", req.Id);
        }

        return new StreamMediaResult(outputPath, "video/mp4");
    }
}
