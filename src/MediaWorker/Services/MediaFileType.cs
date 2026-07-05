namespace MediaWorker.Services;

public enum MediaKind { Image, Heic, Video }

/// <summary>Classifies a file by extension to decide the conversion path in ingest.</summary>
public static class MediaFileType
{
    private static readonly HashSet<string> HeicExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".avi", ".mkv" };

    public static MediaKind Classify(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (HeicExtensions.Contains(ext)) return MediaKind.Heic;
        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        return MediaKind.Image;
    }
}
