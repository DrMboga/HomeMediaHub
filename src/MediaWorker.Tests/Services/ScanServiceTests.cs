using Data.Entities;
using MediaWorker.Sagas;
using MediaWorker.Services;

namespace MediaWorker.Tests.Services;

public sealed class ScanServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hmh-scan-{Guid.NewGuid()}");

    public ScanServiceTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static WorkerDbContext CreateDb() => TestDbContextFactory.Create();

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    [Fact]
    public async Task Discover_SupportedExtensions_ReturnsOnlyMediaFiles()
    {
        Touch(Path.Combine(_tempDir, "photo.jpg"));
        Touch(Path.Combine(_tempDir, "video.mp4"));
        Touch(Path.Combine(_tempDir, "readme.txt"));
        Touch(Path.Combine(_tempDir, "doc.pdf"));

        using var db = CreateDb();
        var files = await new ScanService(db).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.EndsWith("photo.jpg"));
        Assert.Contains(files, f => f.EndsWith("video.mp4"));
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
    public async Task Discover_AllSupportedExtensions_AreIncluded(string extension)
    {
        Touch(Path.Combine(_tempDir, $"file{extension}"));

        using var db = CreateDb();
        var files = await new ScanService(db).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(files);
    }

    [Fact]
    public async Task Discover_AlreadyIndexedFiles_AreExcluded()
    {
        var existingPath = Path.Combine(_tempDir, "existing.jpg");
        var newPath = Path.Combine(_tempDir, "new.jpg");
        Touch(existingPath);
        Touch(newPath);

        using var db = CreateDb();
        db.MediaFiles.Add(new MediaFile { Id = Guid.NewGuid(), FilePath = existingPath });
        await db.SaveChangesAsync();

        var files = await new ScanService(db).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(files);
        Assert.Contains(files, f => f.EndsWith("new.jpg"));
    }

    [Fact]
    public async Task Discover_FilesWithActiveJob_AreExcluded()
    {
        var activePath = Path.Combine(_tempDir, "in-flight.jpg");
        var freePath = Path.Combine(_tempDir, "free.jpg");
        Touch(activePath);
        Touch(freePath);

        using var db = CreateDb();
        db.ProcessingJobs.Add(new ProcessingJob { Id = Guid.NewGuid(), FilePath = activePath, Status = "enriching" });
        await db.SaveChangesAsync();

        var files = await new ScanService(db).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(files);
        Assert.Contains(files, f => f.EndsWith("free.jpg"));
    }

    [Fact]
    public async Task Discover_FilesWithCompletedJob_AreNotExcluded()
    {
        // A completed/failed job is not "active" — the file may need re-discovery.
        var path = Path.Combine(_tempDir, "done.jpg");
        Touch(path);

        using var db = CreateDb();
        db.ProcessingJobs.Add(new ProcessingJob { Id = Guid.NewGuid(), FilePath = path, Status = "completed" });
        await db.SaveChangesAsync();

        var files = await new ScanService(db).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(files);
    }

    [Fact]
    public async Task Discover_DeletedDbEntries_AreStillExcluded()
    {
        // Excluded by media_files presence regardless of soft-delete state.
        var path = Path.Combine(_tempDir, "deleted.jpg");
        Touch(path);

        using var db = CreateDb();
        db.MediaFiles.Add(new MediaFile { Id = Guid.NewGuid(), FilePath = path, IsDeleted = true });
        await db.SaveChangesAsync();

        var files = await new ScanService(db).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Empty(files);
    }

    [Fact]
    public async Task Discover_NestedDirectories_ReturnsAllFiles()
    {
        Touch(Path.Combine(_tempDir, "2023", "Jan", "photo1.jpg"));
        Touch(Path.Combine(_tempDir, "2023", "Feb", "photo2.jpg"));
        Touch(Path.Combine(_tempDir, "2024", "photo3.jpg"));

        using var db = CreateDb();
        var files = await new ScanService(db).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Equal(3, files.Count);
    }

    [Fact]
    public async Task Discover_EmptyDirectory_ReturnsEmptyList()
    {
        using var db = CreateDb();
        var files = await new ScanService(db).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Empty(files);
    }

    [Fact]
    public async Task Discover_ResultsAreSortedAlphabetically()
    {
        Touch(Path.Combine(_tempDir, "c.jpg"));
        Touch(Path.Combine(_tempDir, "a.jpg"));
        Touch(Path.Combine(_tempDir, "b.jpg"));

        using var db = CreateDb();
        var files = await new ScanService(db).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Equal(files.OrderBy(f => f).ToList(), files);
    }

    [Fact]
    public async Task Discover_ExtensionMatchingIsCaseInsensitive()
    {
        Touch(Path.Combine(_tempDir, "PHOTO.JPG"));
        Touch(Path.Combine(_tempDir, "VIDEO.MP4"));

        using var db = CreateDb();
        var files = await new ScanService(db).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Equal(2, files.Count);
    }
}
