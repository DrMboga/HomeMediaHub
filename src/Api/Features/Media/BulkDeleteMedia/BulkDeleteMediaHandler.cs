using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.BulkDeleteMedia;

public record BulkDeleteMediaRequest(List<Guid> Ids) : IRequest<int>;

public sealed class BulkDeleteMediaHandler : IRequestHandler<BulkDeleteMediaRequest, int>
{
    private readonly AppDbContext _db;
    private readonly ILogger<BulkDeleteMediaHandler> _logger;

    public BulkDeleteMediaHandler(AppDbContext db, ILogger<BulkDeleteMediaHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<int> Handle(BulkDeleteMediaRequest req, CancellationToken ct)
    {
        var files = await _db.MediaFiles
            .Include(f => f.Thumbnail)
            .Where(f => req.Ids.Contains(f.Id))
            .ToListAsync(ct);

        foreach (var f in files)
        {
            if (File.Exists(f.FilePath))
            {
                try   { File.Delete(f.FilePath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete file {Path}", f.FilePath); }
            }

            var thumbPath = f.Thumbnail?.ThumbnailPath;
            if (thumbPath is not null && File.Exists(thumbPath))
            {
                try   { File.Delete(thumbPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete thumbnail {Path}", thumbPath); }
            }
        }

        return await _db.MediaFiles
            .Where(f => req.Ids.Contains(f.Id))
            .ExecuteDeleteAsync(ct);
    }
}
