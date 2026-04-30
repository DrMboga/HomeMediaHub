using Api.Data;
using Pgvector;

namespace Api.Features.Media.SaveMedia;

internal static class SaveMediaExtensions
{
    internal static MediaFile ToMediaFile(this SaveMediaRequest req)
    {
        var takenAt = req.TakenAt.HasValue
            ? DateTime.SpecifyKind(req.TakenAt.Value, DateTimeKind.Utc)
            : (DateTime?)null;

        var file = new MediaFile
        {
            Id            = req.Id,
            FilePath      = req.FilePath,
            FileHash      = req.FileHash,
            Source        = req.Source,
            ImmichAssetId = req.ImmichAssetId,
            MimeType      = req.MimeType,
            FileSize      = req.FileSize,
            Width         = req.Width,
            Height        = req.Height,
            DurationSec   = req.DurationSec,
            TakenAt       = takenAt,
        };

        if (req.Lat is not null || req.Lon is not null || req.Address is not null ||
            req.CameraMake is not null || req.CameraModel is not null)
        {
            file.Metadata = new MediaMetadata
            {
                MediaFileId = req.Id,
                Latitude    = req.Lat,
                Longitude   = req.Lon,
                Address     = req.Address,
                CameraMake  = req.CameraMake,
                CameraModel = req.CameraModel,
            };
        }

        file.OriginClass      = req.OriginClass;
        file.OriginConfidence = req.OriginConfidence;

        if (req.Embedding is { Length: 512 })
        {
            file.Embedding = new MediaEmbedding
            {
                MediaFileId = req.Id,
                Embedding   = new Vector(req.Embedding),
            };
        }

        if (req.ThumbnailPath is not null)
        {
            file.Thumbnail = new MediaThumbnail
            {
                MediaFileId   = req.Id,
                ThumbnailPath = req.ThumbnailPath,
            };
        }

        if (req.Tags is { Count: > 0 })
        {
            file.Tags = req.Tags
                .Select(t => new MediaTag
                {
                    Id          = Guid.NewGuid(),
                    MediaFileId = req.Id,
                    Tag         = t.Tag,
                    Confidence  = t.Confidence,
                    Model       = t.Model,
                })
                .ToList();
        }

        return file;
    }
}
