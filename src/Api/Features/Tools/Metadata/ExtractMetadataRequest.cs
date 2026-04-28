using MediatR;

namespace Api.Features.Tools.Metadata;

public record ExtractMetadataRequest(string FilePath, string? OriginalFilePath = null) : IRequest<MetadataResult>;

public record MetadataResult(
    string    MimeType,
    long      FileSize,
    int       Width,
    int       Height,
    double?   DurationSec,
    DateTime? TakenAt,
    double?   Lat,
    double?   Lon,
    string?   CameraMake,
    string?   CameraModel);
