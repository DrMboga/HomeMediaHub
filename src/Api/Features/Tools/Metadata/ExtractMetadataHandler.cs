using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediatR;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace Api.Features.Tools.Metadata;

public sealed class ExtractMetadataHandler : IRequestHandler<ExtractMetadataRequest, MetadataResult>
{
    private static readonly HashSet<string> VideoExtensions = [".mp4", ".mov", ".m4v", ".avi"];

    public async Task<MetadataResult> Handle(ExtractMetadataRequest request, CancellationToken cancellationToken)
    {
        var sourcePath = request.OriginalFilePath ?? request.FilePath;
        var fileInfo = new FileInfo(sourcePath);

        if (VideoExtensions.Contains(Path.GetExtension(sourcePath).ToLowerInvariant()))
            return await ExtractVideoMetadataAsync(request.FilePath, sourcePath, fileInfo, cancellationToken);

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
            MimeType:    DetectMimeType(sourcePath),
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

    private static async Task<MetadataResult> ExtractVideoMetadataAsync(
        string framePath, string originalPath, FileInfo originalFileInfo, CancellationToken ct)
    {
        // Dimensions come from the extracted frame; all other metadata from the original container.
        using var frame = await Image.LoadAsync(framePath, ct);
        var exif = await RunExiftoolJsonAsync(originalPath, ct);

        return new MetadataResult(
            MimeType:    DetectMimeType(originalPath),
            FileSize:    originalFileInfo.Length,
            Width:       frame.Width,
            Height:      frame.Height,
            TakenAt:     ParseDateTime(exif.TryGetProperty("CreateDate", out var dt) ? dt.GetString() : null),
            Lat:         ParseGpsCoordinate(exif, "GPSLatitude",  "GPSLatitudeRef"),
            Lon:         ParseGpsCoordinate(exif, "GPSLongitude", "GPSLongitudeRef"),
            CameraMake:  exif.TryGetProperty("Make",  out var make)  ? make.GetString()  : null,
            CameraModel: exif.TryGetProperty("Model", out var model) ? model.GetString() : null
        );
    }

    private static async Task<JsonElement> RunExiftoolJsonAsync(string filePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("exiftool") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-json");
        psi.ArgumentList.Add("-n"); // numeric GPS — avoids parsing DMS strings
        psi.ArgumentList.Add(filePath);

        using var proc = Process.Start(psi)!;
        var readTask = proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var output = await readTask;

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                return doc.RootElement[0].Clone();
        }
        catch { /* return empty element on parse failure */ }

        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private static double? ParseGpsCoordinate(JsonElement exif, string coordinateTag, string refTag)
    {
        if (!exif.TryGetProperty(coordinateTag, out var prop) || !prop.TryGetDouble(out var value))
            return null;
        var isNegative = exif.TryGetProperty(refTag, out var refProp) && refProp.GetString() is "S" or "W";
        return isNegative ? -value : value;
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
            ".jpg" or ".jpeg"  => "image/jpeg",
            ".png"             => "image/png",
            ".gif"             => "image/gif",
            ".webp"            => "image/webp",
            ".heic" or ".heif" => "image/heic",
            ".mp4"             => "video/mp4",
            ".mov" or ".m4v"   => "video/quicktime",
            _                  => "application/octet-stream"
        };
}
