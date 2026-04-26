using MediatR;

namespace Api.Features.Media.SaveMedia;

public record SaveMediaRequest(
    Guid       Id,
    string     FilePath,
    string     FileHash,
    string     Source,
    string?    ImmichAssetId,
    string     MimeType,
    long?      FileSize,
    int?       Width,
    int?       Height,
    double?    DurationSec,
    DateTime?  TakenAt,
    double?    Lat,
    double?    Lon,
    string?    Address,
    string?    CameraMake,
    string?    CameraModel,
    string?    ThumbnailPath,
    List<SaveMediaTag>? Tags
) : IRequest<SaveMediaResponse>;

public record SaveMediaTag(string Tag, float Confidence, string Model);
public record SaveMediaResponse(Guid Id);
