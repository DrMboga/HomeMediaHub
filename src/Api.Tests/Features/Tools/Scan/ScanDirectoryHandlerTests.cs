using Api.Data;
using Api.Features.Tools.Scan;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests.Features.Tools.Scan;

public sealed class ScanDirectoryHandlerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hmh-scan-{Guid.NewGuid()}");

    public ScanDirectoryHandlerTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    [Fact]
    public async Task Handle_SupportedExtensions_ReturnsOnlyMediaFiles()
    {
        Touch(Path.Combine(_tempDir, "photo.jpg"));
        Touch(Path.Combine(_tempDir, "video.mp4"));
        Touch(Path.Combine(_tempDir, "readme.txt"));
        Touch(Path.Combine(_tempDir, "doc.pdf"));

        using var db = CreateDb();
        var result = await new ScanDirectoryHandler(db)
            .Handle(new ScanDirectoryRequest(_tempDir), CancellationToken.None);

        Assert.Equal(2, result.Files.Count);
        Assert.Contains(result.Files, f => f.EndsWith("photo.jpg"));
        Assert.Contains(result.Files, f => f.EndsWith("video.mp4"));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".heic")]
    [InlineData(".heif")]
    [InlineData(".mp4")]
    [InlineData(".mov")]
    [InlineData(".m4v")]
    [InlineData(".avi")]
    public async Task Handle_AllSupportedExtensions_AreIncluded(string extension)
    {
        Touch(Path.Combine(_tempDir, $"file{extension}"));

        using var db = CreateDb();
        var result = await new ScanDirectoryHandler(db)
            .Handle(new ScanDirectoryRequest(_tempDir), CancellationToken.None);

        Assert.Single(result.Files);
    }

    [Fact]
    public async Task Handle_AlreadyIndexedFiles_AreExcluded()
    {
        var existingPath = Path.Combine(_tempDir, "existing.jpg");
        var newPath = Path.Combine(_tempDir, "new.jpg");
        Touch(existingPath);
        Touch(newPath);

        using var db = CreateDb();
        db.MediaFiles.Add(new MediaFileRecord { Id = Guid.NewGuid(), FilePath = existingPath, IsDeleted = false });
        await db.SaveChangesAsync();

        var result = await new ScanDirectoryHandler(db)
            .Handle(new ScanDirectoryRequest(_tempDir), CancellationToken.None);

        Assert.Single(result.Files);
        Assert.Contains(result.Files, f => f.EndsWith("new.jpg"));
    }

    [Fact]
    public async Task Handle_DeletedDbEntries_AreNotExcluded()
    {
        var path = Path.Combine(_tempDir, "deleted.jpg");
        Touch(path);

        using var db = CreateDb();
        db.MediaFiles.Add(new MediaFileRecord { Id = Guid.NewGuid(), FilePath = path, IsDeleted = true });
        await db.SaveChangesAsync();

        var result = await new ScanDirectoryHandler(db)
            .Handle(new ScanDirectoryRequest(_tempDir), CancellationToken.None);

        Assert.Single(result.Files);
    }

    [Fact]
    public async Task Handle_NestedDirectories_ReturnsAllFiles()
    {
        Touch(Path.Combine(_tempDir, "2023", "Jan", "photo1.jpg"));
        Touch(Path.Combine(_tempDir, "2023", "Feb", "photo2.jpg"));
        Touch(Path.Combine(_tempDir, "2024", "photo3.jpg"));

        using var db = CreateDb();
        var result = await new ScanDirectoryHandler(db)
            .Handle(new ScanDirectoryRequest(_tempDir), CancellationToken.None);

        Assert.Equal(3, result.Files.Count);
    }

    [Fact]
    public async Task Handle_EmptyDirectory_ReturnsEmptyList()
    {
        using var db = CreateDb();
        var result = await new ScanDirectoryHandler(db)
            .Handle(new ScanDirectoryRequest(_tempDir), CancellationToken.None);

        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task Handle_ResultsAreSortedAlphabetically()
    {
        Touch(Path.Combine(_tempDir, "c.jpg"));
        Touch(Path.Combine(_tempDir, "a.jpg"));
        Touch(Path.Combine(_tempDir, "b.jpg"));

        using var db = CreateDb();
        var result = await new ScanDirectoryHandler(db)
            .Handle(new ScanDirectoryRequest(_tempDir), CancellationToken.None);

        Assert.Equal(result.Files.OrderBy(f => f).ToList(), result.Files);
    }

    [Fact]
    public async Task Handle_ExtensionMatchingIsCaseInsensitive()
    {
        Touch(Path.Combine(_tempDir, "PHOTO.JPG"));
        Touch(Path.Combine(_tempDir, "VIDEO.MP4"));

        using var db = CreateDb();
        var result = await new ScanDirectoryHandler(db)
            .Handle(new ScanDirectoryRequest(_tempDir), CancellationToken.None);

        Assert.Equal(2, result.Files.Count);
    }
}
