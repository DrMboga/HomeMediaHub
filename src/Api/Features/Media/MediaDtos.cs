using Data.Entities;

namespace Api.Features.Media;

public record MediaListItemDto(
    Guid Id,
    string MimeType,
    string? ThumbnailPath,
    DateTime? TakenAt,
    bool IsFavorite,
    bool IsVideo,
    string? OriginClass,
    double? OriginConfidence,
    string? Address,
    // Cosine distance to the query vector — semantic search only, null everywhere else.
    // Lower is closer. Surfaced so relevance can be judged instead of guessed: CLIP
    // packs its whole useful range into ~0.70–0.80, so a long result list is normal
    // even when only the first few entries are real matches.
    double? Distance = null
);

public record MediaDetailDto(
    Guid Id,
    string FilePath,
    string MimeType,
    long? FileSize,
    int? Width,
    int? Height,
    double? DurationSec,
    DateTime? TakenAt,
    bool IsFavorite,
    string? OriginClass,
    double? OriginConfidence,
    string? CameraMake,
    string? CameraModel,
    double? Latitude,
    double? Longitude,
    string? Address,
    List<TagDto> Tags
);

public record TagDto(string Tag, float Confidence, string Model);

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class MediaFileExtensions
{
    public static MediaListItemDto ToListItemDto(this MediaFile f) =>
        new(f.Id, f.MimeType, f.Thumbnail?.ThumbnailPath, f.TakenAt,
            f.IsFavorite, f.DurationSec.HasValue, f.OriginClass, f.OriginConfidence,
            f.Metadata?.Address);
}
