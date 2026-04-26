using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.SaveMedia;

public sealed class SaveMediaHandler : IRequestHandler<SaveMediaRequest, SaveMediaResponse>
{
    private readonly AppDbContext _db;
    public SaveMediaHandler(AppDbContext db) => _db = db;

    public async Task<SaveMediaResponse> Handle(SaveMediaRequest req, CancellationToken ct)
    {
        // EXIF dates have no timezone — treat as UTC for storage.
        var takenAt = req.TakenAt.HasValue
            ? DateTime.SpecifyKind(req.TakenAt.Value, DateTimeKind.Utc)
            : (DateTime?)null;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        await _db.Database.ExecuteSqlAsync($"""
            INSERT INTO media_files
                (id, file_path, file_hash, source, immich_asset_id, mime_type,
                 file_size, width, height, duration_sec, taken_at, is_deleted, is_favorite)
            VALUES
                ({req.Id}, {req.FilePath}, {req.FileHash}, {req.Source}, {req.ImmichAssetId},
                 {req.MimeType}, {req.FileSize}, {req.Width}, {req.Height},
                 {req.DurationSec}, {takenAt}, FALSE, FALSE)
            """, ct);

        if (req.Lat is not null || req.Lon is not null || req.Address is not null ||
            req.CameraMake is not null || req.CameraModel is not null)
        {
            await _db.Database.ExecuteSqlAsync($"""
                INSERT INTO media_metadata (media_file_id, latitude, longitude, address, camera_make, camera_model)
                VALUES ({req.Id}, {req.Lat}, {req.Lon}, {req.Address}, {req.CameraMake}, {req.CameraModel})
                """, ct);
        }

        if (req.ThumbnailPath is not null)
        {
            await _db.Database.ExecuteSqlAsync($"""
                INSERT INTO media_thumbnails (media_file_id, thumbnail_path)
                VALUES ({req.Id}, {req.ThumbnailPath})
                """, ct);
        }

        if (req.Tags is { Count: > 0 })
        {
            foreach (var tag in req.Tags)
            {
                await _db.Database.ExecuteSqlAsync($"""
                    INSERT INTO media_tags (id, media_file_id, tag, confidence, model)
                    VALUES ({Guid.NewGuid()}, {req.Id}, {tag.Tag}, {tag.Confidence}, {tag.Model})
                    """, ct);
            }
        }

        await tx.CommitAsync(ct);
        return new SaveMediaResponse(req.Id);
    }
}
