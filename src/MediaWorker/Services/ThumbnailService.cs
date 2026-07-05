using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace MediaWorker.Services;

/// <summary>
/// Generates a 400px WebP thumbnail under <c>/thumbnails/{id}.webp</c> (ported from the old
/// <c>GenerateThumbnailHandler</c>). Returns the thumbnail path.
/// </summary>
public sealed class ThumbnailService
{
    public async Task<string> GenerateAsync(string filePath, Guid mediaFileId, CancellationToken ct)
    {
        var thumbnailPath = $"/thumbnails/{mediaFileId}.webp";
        using var image = await Image.LoadAsync(filePath, ct);
        image.Mutate(x => x
            .AutoOrient()
            .Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(400, 400)
            }));
        await image.SaveAsync(thumbnailPath, new WebpEncoder { Quality = 80 }, ct);
        return thumbnailPath;
    }
}
