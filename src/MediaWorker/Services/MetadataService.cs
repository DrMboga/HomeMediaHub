using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace MediaWorker.Services;

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

/// <summary>
/// EXIF/container metadata extraction (ported from the old <c>ExtractMetadataHandler</c>).
/// Uses <c>Image.IdentifyAsync</c> (header only, no pixel decode) for images and
/// <c>exiftool -json</c> (30 s kill guard) for videos.
/// </summary>
public sealed class MetadataService
{
    private static readonly HashSet<string> VideoExtensions = [".mp4", ".mov", ".m4v", ".avi", ".mkv"];

    /// <param name="filePath">The file to read pixel dimensions from — the converted JPEG/frame
    /// for HEIC/video, else the original.</param>
    /// <param name="originalFilePath">The original source file for EXIF/date/GPS. When null,
    /// <paramref name="filePath"/> is used for both.</param>
    public async Task<MetadataResult> ExtractAsync(string filePath, string? originalFilePath, CancellationToken ct)
    {
        var sourcePath = originalFilePath ?? filePath;
        var fileInfo = new FileInfo(sourcePath);

        if (VideoExtensions.Contains(Path.GetExtension(sourcePath).ToLowerInvariant()))
            return await ExtractVideoMetadataAsync(filePath, sourcePath, fileInfo, ct);

        // IdentifyAsync reads only the image header — no pixel decoding, far less memory than LoadAsync.
        var info = await Image.IdentifyAsync(filePath, ct)
            ?? throw new InvalidOperationException($"Cannot identify image: {filePath}");
        var exif = info.Metadata.ExifProfile;

        var orientation = GetOrientation(exif);
        // EXIF orientations 5-8 involve a 90° or 270° rotation, so raw pixel
        // dimensions are transposed relative to the display dimensions.
        var (displayWidth, displayHeight) = orientation is 5 or 6 or 7 or 8
            ? (info.Height, info.Width)
            : (info.Width, info.Height);

        var takenAt = ParseDateTime(TryGet(exif, ExifTag.DateTimeOriginal))
                   ?? ParseDateTime(TryGet(exif, ExifTag.DateTimeDigitized))
                   ?? EarliestFilesystemDate(fileInfo);

        return new MetadataResult(
            MimeType:    DetectMimeType(sourcePath),
            FileSize:    fileInfo.Length,
            Width:       displayWidth,
            Height:      displayHeight,
            DurationSec: null,
            TakenAt:     takenAt,
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
        var frameInfo = await Image.IdentifyAsync(framePath, ct)
            ?? throw new InvalidOperationException($"Cannot identify frame: {framePath}");
        var exif = await RunExiftoolJsonAsync(originalPath, ct);

        var durationSec = exif.TryGetProperty("Duration", out var dur) && dur.TryGetDouble(out var d) ? d : (double?)null;

        var takenAt = ParseExiftoolDate(exif, "DateTimeOriginal")
                   ?? ParseExiftoolDate(exif, "CreateDate")
                   ?? EarliestFilesystemDate(originalFileInfo);

        return new MetadataResult(
            MimeType:    DetectMimeType(originalPath),
            FileSize:    originalFileInfo.Length,
            Width:       frameInfo.Width,
            Height:      frameInfo.Height,
            DurationSec: durationSec,
            TakenAt:     takenAt,
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var readTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        var output = await readTask;

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                return doc.RootElement[0].Clone();
        }
        catch { /* return empty element on parse failure */ }

        using var emptyFallback = JsonDocument.Parse("{}");
        return emptyFallback.RootElement.Clone();
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

    // EXIF DateTimeOriginal is naive wall-clock — the time the camera's own display showed,
    // with no zone attached. Tag it UTC without shifting it: ToUniversalTime() treats a
    // Kind=Unspecified value as the *host's* local time, so the same photo would land on a
    // different taken_at depending on where the worker runs. The frontend renders UTC to match
    // (src/Frontend/src/utils/mediaDate.ts).
    private static DateTime? ParseDateTime(string? value) =>
        DateTime.TryParseExact(value, "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : null;

    // ExifTool dates: "yyyy:MM:dd HH:mm:ss" or "yyyy:MM:dd HH:mm:ss+HH:mm". Where an offset is
    // present we deliberately keep the wall-clock half and drop the offset, so a photo taken at
    // 17:00 in Crete reads 17:00 rather than 15:00 — and so offset-carrying HEICs stay consistent
    // with the far more common zone-less JPEGs above.
    private static DateTime? ParseExiftoolDate(JsonElement exif, string tag)
    {
        if (!exif.TryGetProperty(tag, out var prop)) return null;
        var s = prop.GetString();
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTimeOffset.TryParseExact(s,
                ["yyyy:MM:dd HH:mm:sszzz", "yyyy:MM:dd HH:mm:ss"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Utc);
        return null;
    }

    // Use LastWriteTime (mtime) as the filesystem date fallback — rsync --times / scp -p preserve
    // it from the source. On Linux CreationTime is unreliable (min(atime,mtime,ctime)), so ignored.
    private static DateTime? EarliestFilesystemDate(FileInfo fi)
    {
        var mtime = fi.LastWriteTimeUtc;
        return mtime.Year >= 1980 ? mtime : null;
    }

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
            ".avi"             => "video/x-msvideo",
            ".mkv"             => "video/x-matroska",
            _                  => "application/octet-stream"
        };
}
