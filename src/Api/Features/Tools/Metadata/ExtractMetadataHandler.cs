using System.Globalization;
using MediatR;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace Api.Features.Tools.Metadata;

public sealed class ExtractMetadataHandler : IRequestHandler<ExtractMetadataRequest, MetadataResult>
{
    public async Task<MetadataResult> Handle(ExtractMetadataRequest request, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(request.FilePath);
        using var image = await Image.LoadAsync(request.FilePath, cancellationToken);
        var exif = image.Metadata.ExifProfile;

        var orientation = GetOrientation(exif);
        // EXIF orientations 5-8 involve a 90° or 270° rotation, so raw pixel
        // dimensions are transposed relative to the display dimensions.
        // 5 = 90° CW + horizontal flip, 6 = 90° CW (iPhone portrait),
        // 7 = 90° CCW + horizontal flip, 8 = 90° CCW (iPhone portrait upside-down).
        var (displayWidth, displayHeight) = orientation is 5 or 6 or 7 or 8
            ? (image.Height, image.Width)
            : (image.Width, image.Height);

        return new MetadataResult(
            MimeType:    DetectMimeType(request.FilePath),
            FileSize:    fileInfo.Length,
            Width:       displayWidth,
            Height:      displayHeight,
            TakenAt:     ParseDateTime(TryGet(exif, ExifTag.DateTimeOriginal)),
            Lat:         ToDecimalDegrees(TryGet(exif, ExifTag.GPSLatitude),
                                          TryGet(exif, ExifTag.GPSLatitudeRef)),
            Lon:         ToDecimalDegrees(TryGet(exif, ExifTag.GPSLongitude),
                                          TryGet(exif, ExifTag.GPSLongitudeRef)),
            CameraMake:  TryGet(exif, ExifTag.Make),
            CameraModel: TryGet(exif, ExifTag.Model)
        );
    }

    private static ushort GetOrientation(ExifProfile? exif)
    {
        if (exif is null) return 1;
        exif.TryGetValue(ExifTag.Orientation, out var entry);
        return entry?.Value ?? 1;
    }

    private static TValue? TryGet<TValue>(ExifProfile? exif, ExifTag<TValue> tag) where TValue : class
    {
        if (exif is null) return null;
        exif.TryGetValue(tag, out var entry);
        return entry?.Value;
    }

    private static DateTime? ParseDateTime(string? value) =>
        DateTime.TryParseExact(value, "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt : null;

    private static double? ToDecimalDegrees(Rational[]? coord, string? reference)
    {
        if (coord is null || coord.Length < 3) return null;
        var degrees = (double)coord[0].Numerator / coord[0].Denominator;
        var minutes = (double)coord[1].Numerator / coord[1].Denominator;
        var seconds = (double)coord[2].Numerator / coord[2].Denominator;
        var result  = degrees + minutes / 60.0 + seconds / 3600.0;
        return reference is "S" or "W" ? -result : result;
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
