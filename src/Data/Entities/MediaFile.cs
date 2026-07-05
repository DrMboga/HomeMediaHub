namespace Data.Entities;

public sealed class MediaFile
{
    public Guid Id { get; set; }
    public string FilePath { get; set; } = "";
    public string FileHash { get; set; } = "";
    public string Source { get; set; } = "";
    public string? ImmichAssetId { get; set; }
    public string MimeType { get; set; } = "";
    public long? FileSize { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSec { get; set; }
    public DateTime? TakenAt { get; set; }
    public bool IsDeleted { get; set; }
    public bool IsFavorite { get; set; }
    public string? OriginClass { get; set; }
    public double? OriginConfidence { get; set; }
    public bool OriginReviewed { get; set; }
    public DateTime CreatedAt { get; set; }

    public MediaMetadata? Metadata { get; set; }
    public MediaThumbnail? Thumbnail { get; set; }
    public MediaEmbedding? Embedding { get; set; }
    public List<MediaTag> Tags { get; set; } = [];
}
