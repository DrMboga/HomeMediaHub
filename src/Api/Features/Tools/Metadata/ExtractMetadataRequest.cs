using System.Text.Json;
using MediatR;

namespace Api.Features.Tools.Metadata;

public record ExtractMetadataRequest(string FilePath) : IRequest<MetadataResult>;

public record MetadataResult(
    string      MimeType,
    long        FileSize,
    int?        Width,
    int?        Height,
    DateTime?   TakenAt,
    double?     Lat,
    double?     Lon,
    string?     CameraMake,
    string?     CameraModel,
    JsonElement ExifJson);
