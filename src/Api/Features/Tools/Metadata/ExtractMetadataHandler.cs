using System.Text.Json;
using MediatR;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;

namespace Api.Features.Tools.Metadata;

public sealed class ExtractMetadataHandler : IRequestHandler<ExtractMetadataRequest, MetadataResult>
{
    public Task<MetadataResult> Handle(ExtractMetadataRequest request, CancellationToken cancellationToken)
        => Task.Run(() => Extract(request.FilePath), cancellationToken);

    private static MetadataResult Extract(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var dirs = ImageMetadataReader.ReadMetadata(filePath);

        var jpeg = dirs.OfType<JpegDirectory>().FirstOrDefault();
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub  = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps  = dirs.OfType<GpsDirectory>().FirstOrDefault();

        int? width  = jpeg is not null && jpeg.TryGetInt32(JpegDirectory.TagImageWidth,  out var jw) ? jw : null;
        int? height = jpeg is not null && jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out var jh) ? jh : null;

        DateTime? takenAt = null;
        if (sub is not null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
            takenAt = dt;

        var location = gps?.GetGeoLocation();

        var exifData = dirs
            .SelectMany(d => d.Tags)
            .Select(t => new { directory = t.DirectoryName, tag = t.Name, value = t.Description })
            .ToList();

        return new MetadataResult(
            MimeType:    DetectMimeType(filePath),
            FileSize:    fileInfo.Length,
            Width:       width,
            Height:      height,
            TakenAt:     takenAt,
            Lat:         location?.Latitude,
            Lon:         location?.Longitude,
            CameraMake:  ifd0?.GetString(ExifDirectoryBase.TagMake),
            CameraModel: ifd0?.GetString(ExifDirectoryBase.TagModel),
            ExifJson:    JsonSerializer.SerializeToElement(exifData)
        );
    }

    private static string DetectMimeType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            _                 => "application/octet-stream"
        };
}
