using Api.Data;
using Api.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.DisplayMedia;

public record DisplayMediaRequest(Guid Id, string DisplayCacheDir) : IRequest<DisplayMediaResult?>;

public record DisplayMediaResult(string FilePath, string ContentType);

public sealed class DisplayMediaHandler : IRequestHandler<DisplayMediaRequest, DisplayMediaResult?>
{
    // A single HEIC decode is fast; this is an upper bound against a wedged child, not a budget.
    private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(2);

    private readonly AppDbContext _db;
    private readonly ILogger<DisplayMediaHandler> _logger;

    public DisplayMediaHandler(AppDbContext db, ILogger<DisplayMediaHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<DisplayMediaResult?> Handle(DisplayMediaRequest req, CancellationToken ct)
    {
        var f = await _db.MediaFiles
            .Where(x => x.Id == req.Id && !x.IsDeleted)
            .Select(x => new { x.FilePath, x.MimeType })
            .FirstOrDefaultAsync(ct);

        if (f is null || !File.Exists(f.FilePath))
            return null;

        // Non-HEIC files are browser-renderable — serve directly
        if (!f.MimeType.StartsWith("image/heic", StringComparison.OrdinalIgnoreCase) &&
            !f.MimeType.StartsWith("image/heif", StringComparison.OrdinalIgnoreCase))
            return new DisplayMediaResult(f.FilePath, f.MimeType);

        // HEIC — return cached JPEG if available
        var cachePath = Path.Combine(req.DisplayCacheDir, $"{req.Id}.jpg");
        if (File.Exists(cachePath))
            return new DisplayMediaResult(cachePath, "image/jpeg");

        // Convert on demand. Runs through ProcessRunner for the drained pipes and the
        // kill-on-timeout that this handler previously lacked — the same shape of bug that
        // hung large video transcodes, latent here only because heif-convert is quiet.
        Directory.CreateDirectory(req.DisplayCacheDir);

        // Convert to a unique temp file and move on success: the path is served straight off
        // disk on the next request, so a partial JPEG must never be left at the cache path.
        var tempPath = Path.Combine(req.DisplayCacheDir, $"{req.Id}.{Guid.NewGuid():N}.part.jpg");

        var psi = new System.Diagnostics.ProcessStartInfo("heif-convert");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("90");
        psi.ArgumentList.Add(f.FilePath);
        psi.ArgumentList.Add(tempPath);

        var result = await ProcessRunner.RunAsync(psi, ConvertTimeout);

        if (!result.Succeeded || !File.Exists(tempPath))
        {
            _logger.LogError("heif-convert failed for {MediaId} (timedOut={TimedOut}, exit={ExitCode}): {Error}",
                req.Id, result.TimedOut, result.ExitCode, result.StandardError);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            return null;
        }

        File.Move(tempPath, cachePath, overwrite: true);
        return new DisplayMediaResult(cachePath, "image/jpeg");
    }
}
