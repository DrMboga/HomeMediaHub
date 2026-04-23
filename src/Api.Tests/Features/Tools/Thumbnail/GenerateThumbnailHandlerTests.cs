using Api.Features.Tools.Thumbnail;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Api.Tests.Features.Tools.Thumbnail;

// The handler writes to /thumbnails/{id}.webp — a Docker volume mount in production.
// On Windows without write access to that path the tests return early (no-op).
// On Linux they run fully.
public sealed class GenerateThumbnailHandlerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hmh-thumb-{Guid.NewGuid()}");
    private readonly string _mediaFileId = Guid.NewGuid().ToString();
    private readonly GenerateThumbnailHandler _handler = new();
    private static readonly string ThumbnailsRoot = Path.GetFullPath("/thumbnails");

    public GenerateThumbnailHandlerTests()
    {
        Directory.CreateDirectory(_tempDir);
        try { Directory.CreateDirectory(ThumbnailsRoot); }
        catch { /* no-op — tests guard with ThumbnailsDirAvailable() */ }
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
        var path = Path.Combine(ThumbnailsRoot, $"{_mediaFileId}.webp");
        if (File.Exists(path)) File.Delete(path);
    }

    private static bool ThumbnailsDirAvailable() => Directory.Exists(ThumbnailsRoot);

    private async Task<string> SaveSourceJpegAsync(int width, int height)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid()}.jpg");
        using var image = new Image<Rgba32>(width, height);
        await image.SaveAsJpegAsync(path);
        return path;
    }

    [Fact]
    public async Task Handle_LargeImage_ResizesToMax400px()
    {
        if (!ThumbnailsDirAvailable()) return;
        var sourcePath = await SaveSourceJpegAsync(1200, 900);

        var result = await _handler.Handle(new GenerateThumbnailRequest(sourcePath, _mediaFileId), CancellationToken.None);

        using var thumbnail = await Image.LoadAsync(result.ThumbnailPath);
        Assert.True(thumbnail.Width <= 400);
        Assert.True(thumbnail.Height <= 400);
    }

    [Fact]
    public async Task Handle_LandscapeImage_PreservesAspectRatio()
    {
        if (!ThumbnailsDirAvailable()) return;
        var sourcePath = await SaveSourceJpegAsync(800, 400); // 2:1 ratio

        var result = await _handler.Handle(new GenerateThumbnailRequest(sourcePath, _mediaFileId), CancellationToken.None);

        using var thumbnail = await Image.LoadAsync(result.ThumbnailPath);
        Assert.Equal(400, thumbnail.Width);
        Assert.Equal(200, thumbnail.Height);
    }

    [Fact]
    public async Task Handle_SmallImage_UpscalesToFit400px()
    {
        // ResizeMode.Max scales the longest side to 400, upscaling small images too
        if (!ThumbnailsDirAvailable()) return;
        var sourcePath = await SaveSourceJpegAsync(100, 80); // 5:4 ratio

        var result = await _handler.Handle(new GenerateThumbnailRequest(sourcePath, _mediaFileId), CancellationToken.None);

        using var thumbnail = await Image.LoadAsync(result.ThumbnailPath);
        Assert.Equal(400, thumbnail.Width);
        Assert.Equal(320, thumbnail.Height); // 100→400 ×4, 80→320 ×4
    }

    [Fact]
    public async Task Handle_ReturnsExpectedThumbnailPath()
    {
        if (!ThumbnailsDirAvailable()) return;
        var sourcePath = await SaveSourceJpegAsync(200, 200);

        var result = await _handler.Handle(new GenerateThumbnailRequest(sourcePath, _mediaFileId), CancellationToken.None);

        Assert.Equal($"/thumbnails/{_mediaFileId}.webp", result.ThumbnailPath);
    }

    [Fact]
    public async Task Handle_CreatesWebpFileOnDisk()
    {
        if (!ThumbnailsDirAvailable()) return;
        var sourcePath = await SaveSourceJpegAsync(200, 200);

        var result = await _handler.Handle(new GenerateThumbnailRequest(sourcePath, _mediaFileId), CancellationToken.None);

        Assert.True(File.Exists(Path.GetFullPath(result.ThumbnailPath)));
    }
}
