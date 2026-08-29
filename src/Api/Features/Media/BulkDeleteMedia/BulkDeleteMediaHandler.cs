using Api.Data;
using Api.Services;
using Data.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.BulkDeleteMedia;

public record BulkDeleteMediaRequest(List<Guid> Ids) : IRequest<int>;

public sealed class BulkDeleteMediaHandler : IRequestHandler<BulkDeleteMediaRequest, int>
{
    private readonly AppDbContext _db;
    private readonly ILogger<BulkDeleteMediaHandler> _logger;
    private readonly DeletedHashRecorder _deletedHashes;

    public BulkDeleteMediaHandler(
        AppDbContext db,
        ILogger<BulkDeleteMediaHandler> logger,
        DeletedHashRecorder deletedHashes)
    {
        _db            = db;
        _logger        = logger;
        _deletedHashes = deletedHashes;
    }

    public async Task<int> Handle(BulkDeleteMediaRequest req, CancellationToken ct)
    {
        var files = await _db.MediaFiles
            .Include(f => f.Thumbnail)
            .Where(f => req.Ids.Contains(f.Id))
            .ToListAsync(ct);

        // Before the files go: dedup matches on hash, so removing the indexed copy would
        // otherwise un-shadow every duplicate still on disk for the next scan to ingest as new.
        await _deletedHashes.RecordAsync(files, ct);

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
