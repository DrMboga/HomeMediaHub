using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.DisplayMedia;

public record DisplayMediaRequest(Guid Id, string DisplayCacheDir) : IRequest<DisplayMediaResult?>;

public record DisplayMediaResult(string FilePath, string ContentType);

public sealed class DisplayMediaHandler : IRequestHandler<DisplayMediaRequest, DisplayMediaResult?>
{
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

        // Convert on demand
        Directory.CreateDirectory(req.DisplayCacheDir);

        var psi = new System.Diagnostics.ProcessStartInfo("heif-convert")
        {
            RedirectStandardError = true,
            UseShellExecute       = false,
        };
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("90");
        psi.ArgumentList.Add(f.FilePath);
        psi.ArgumentList.Add(cachePath);

        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0 || !File.Exists(cachePath))
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            _logger.LogError("heif-convert failed for {MediaId}: {Error}", req.Id, err);
            return null;
        }

        return new DisplayMediaResult(cachePath, "image/jpeg");
    }
}
