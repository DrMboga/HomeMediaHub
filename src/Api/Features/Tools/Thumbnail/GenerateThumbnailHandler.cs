using MediatR;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Api.Features.Tools.Thumbnail;

public sealed class GenerateThumbnailHandler : IRequestHandler<GenerateThumbnailRequest, ThumbnailResult>
{
    public async Task<ThumbnailResult> Handle(GenerateThumbnailRequest request, CancellationToken cancellationToken)
    {
        var thumbnailPath = $"/thumbnails/{request.MediaFileId}.webp";
        using var image = await Image.LoadAsync(request.FilePath, cancellationToken);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(400, 400)
        }));
        await image.SaveAsync(thumbnailPath, new WebpEncoder { Quality = 80 }, cancellationToken);
        return new ThumbnailResult(thumbnailPath);
    }
}
