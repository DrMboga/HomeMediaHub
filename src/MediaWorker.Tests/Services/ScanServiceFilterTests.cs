using MediaWorker.Sagas;
using MediaWorker.Services;

namespace MediaWorker.Tests.Services;

/// <summary>
/// Phase 14: directory exclusion and the in-flight file guard. Kept apart from
/// <see cref="ScanServiceTests"/>, whose fixtures all construct a bare <c>ScanService(db)</c> and
/// therefore deliberately run with <see cref="ScanOptions.Empty"/>.
/// </summary>
public sealed class ScanServiceFilterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hmh-scanfilter-{Guid.NewGuid()}");

    public ScanServiceFilterTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static WorkerDbContext CreateDb() => TestDbContextFactory.Create();

    // Backdated well past any MinFileAge under test, so age is only in play where a test says so.
    private string Touch(params string[] segments)
    {
        var path = Path.Combine([_tempDir, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not empty");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        return path;
    }

    private string Dir(params string[] segments) => Path.Combine([_tempDir, .. segments]);

    // ── directory exclusion ────────────────────────────────────────────────────

    [Fact]
    public async Task Discover_ExcludedDirectory_IsNotDescendedInto()
    {
        Touch("iPhone", "library", "IMG_1.heic");
        Touch("iPhone", "thumbs", "a", "b", "IMG_1_preview.jpeg");
        Touch("iPhone", "encoded-video", "c", "d", "clip.mp4");

        using var db = CreateDb();
        var options = ScanOptions.Create(
            [Dir("iPhone", "thumbs"), Dir("iPhone", "encoded-video")], TimeSpan.Zero);

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.EndsWith("IMG_1.heic", result.Files[0]);
        Assert.Equal(2, result.PrunedDirectories);
    }

    [Fact]
    public async Task Discover_PrunedDirectoriesCountsDirectoriesNotFiles()
    {
        // One excluded root holding many files must count once — the point of pruning is not
        // looking inside, so a file count there is not available and would be a lie if invented.
        for (var i = 0; i < 5; i++) Touch("thumbs", $"IMG_{i}.jpeg");
        Touch("keep.jpg");

        using var db = CreateDb();
        var options = ScanOptions.Create([Dir("thumbs")], TimeSpan.Zero);

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.Equal(1, result.PrunedDirectories);
    }

    [Fact]
    public async Task Discover_SiblingDirectoryWithSameLeafName_IsNotExcluded()
    {
        // The reason exclusions are absolute paths, not directory names: a real Photo/2019/upload
        // must survive while Immich's /photo/iPhone/upload is pruned.
        Touch("iPhone", "upload", "staged.heic");
        Touch("2019", "upload", "real.jpg");

        using var db = CreateDb();
        var options = ScanOptions.Create([Dir("iPhone", "upload")], TimeSpan.Zero);

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.EndsWith("real.jpg", result.Files[0]);
        Assert.Equal(1, result.PrunedDirectories);
    }

    [Fact]
    public async Task Discover_ExcludedPathIsNotAPrefixMatch()
    {
        // "/photo/iPhone/upload" must not swallow "/photo/iPhone/uploads".
        Touch("uploads", "keep.jpg");

        using var db = CreateDb();
        var options = ScanOptions.Create([Dir("upload")], TimeSpan.Zero);

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.Equal(0, result.PrunedDirectories);
    }

    [Fact]
    public async Task Discover_NestedDirectoryUnderExcludedRoot_IsAlsoExcluded()
    {
        Touch("iPhone", "thumbs", "a9", "e1", "deep.jpeg");

        using var db = CreateDb();
        var options = ScanOptions.Create([Dir("iPhone", "thumbs")], TimeSpan.Zero);

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.PrunedDirectories);
    }

    [Fact]
    public async Task Discover_ExcludedDirectoryThatDoesNotExist_IsNotAnError()
    {
        Touch("keep.jpg");

        using var db = CreateDb();
        var options = ScanOptions.Create(
            [Dir("iPhone", "thumbs"), Dir("nope", "at", "all")], TimeSpan.Zero);

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.Equal(0, result.PrunedDirectories);
    }

    [Fact]
    public async Task Discover_NoExclusions_BehavesAsBefore()
    {
        // Stage's configuration: empty list, so the whole tree is visible.
        Touch("iPhone", "thumbs", "IMG_1_preview.jpeg");
        Touch("iPhone", "library", "IMG_1.heic");

        using var db = CreateDb();
        var result = await new ScanService(db, ScanOptions.Empty)
            .DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Equal(2, result.Files.Count);
        Assert.Equal(0, result.PrunedDirectories);
    }

    [Fact]
    public async Task Discover_ScanRootItselfExcluded_ReturnsNothing()
    {
        Touch("keep.jpg");

        using var db = CreateDb();
        var options = ScanOptions.Create([_tempDir], TimeSpan.Zero);

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.PrunedDirectories);
    }

    [Fact]
    public async Task Discover_ExcludePathWithTrailingSeparator_StillMatches()
    {
        Touch("thumbs", "IMG_1.jpeg");
        Touch("keep.jpg");

        using var db = CreateDb();
        var options = ScanOptions.Create([Dir("thumbs") + Path.DirectorySeparatorChar], TimeSpan.Zero);

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.Equal(1, result.PrunedDirectories);
    }

    [Fact]
    public void ScanOptions_UnusablePathEntries_AreDroppedNotThrown()
    {
        // One bad entry in the env file must not take the worker down at startup.
        var options = ScanOptions.Create(["", "   ", Dir("thumbs")], TimeSpan.Zero);

        Assert.Single(options.ExcludeDirs);
        Assert.EndsWith("thumbs", options.ExcludeDirs[0]);
    }

    // ── in-flight file guard ───────────────────────────────────────────────────

    [Fact]
    public async Task Discover_FileWrittenWithinMinAge_IsSkippedAndCounted()
    {
        var fresh = Path.Combine(_tempDir, "in-flight.heic");
        File.WriteAllText(fresh, "half written");   // mtime = now
        Touch("settled.heic");

        using var db = CreateDb();
        var options = ScanOptions.Create(null, TimeSpan.FromMinutes(2));

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.EndsWith("settled.heic", result.Files[0]);
        Assert.Equal(1, result.SkippedTooRecent);
    }

    [Fact]
    public async Task Discover_BackdatedFile_IsReturned()
    {
        // Immich utimes each upload back to the phone's fileModifiedAt once the write completes,
        // so a finished file is never "too recent" no matter when it was uploaded.
        Touch("IMG_5181.heic");

        using var db = CreateDb();
        var options = ScanOptions.Create(null, TimeSpan.FromMinutes(2));

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.Equal(0, result.SkippedTooRecent);
    }

    [Fact]
    public async Task Discover_MinFileAgeZero_DisablesTheCheck()
    {
        File.WriteAllText(Path.Combine(_tempDir, "brand-new.jpg"), "x");

        using var db = CreateDb();
        var result = await new ScanService(db, ScanOptions.Create(null, TimeSpan.Zero))
            .DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Single(result.Files);
        Assert.Equal(0, result.SkippedTooRecent);
    }

    [Fact]
    public async Task Discover_FreshZeroLengthFile_CountsAsTooRecentNotEmpty()
    {
        // A file mid-write is momentarily zero bytes. Attributing it to the dead-source-data
        // counter would put a wrong number in the log that nobody could later explain.
        File.WriteAllText(Path.Combine(_tempDir, "starting.heic"), "");

        using var db = CreateDb();
        var options = ScanOptions.Create(null, TimeSpan.FromMinutes(2));

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.SkippedTooRecent);
        Assert.Equal(0, result.SkippedEmpty);
    }

    [Fact]
    public async Task Discover_OldZeroLengthFile_StillCountsAsEmpty()
    {
        var path = Path.Combine(_tempDir, "dead.heic");
        File.WriteAllText(path, "");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));

        using var db = CreateDb();
        var options = ScanOptions.Create(null, TimeSpan.FromMinutes(2));

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.SkippedEmpty);
        Assert.Equal(0, result.SkippedTooRecent);
    }

    [Fact]
    public async Task Discover_FreshSidecar_IsCountedAsSidecarNotTooRecent()
    {
        // Name-based filters run before the age check: a ._ file is never going to become valid.
        File.WriteAllText(Path.Combine(_tempDir, "._IMG_1.HEIC"), "AppleDouble");

        using var db = CreateDb();
        var options = ScanOptions.Create(null, TimeSpan.FromMinutes(2));

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Equal(1, result.SkippedSidecars);
        Assert.Equal(0, result.SkippedTooRecent);
    }

    // ── the two together, on the shape of the real prod tree ───────────────────

    [Fact]
    public async Task Discover_ImmichTreeShape_QueuesOnlyTheOriginals()
    {
        Touch("iPhone", "library", "mike", "2026", "2026-04-11", "IMG_5181.heic");
        Touch("iPhone", "library", "mike", "2026", "2026-04-11", "IMG_5147.mov");
        Touch("iPhone", "thumbs", "93ca", "a5", "fa", "asset_preview.jpeg");
        Touch("iPhone", "thumbs", "93ca", "a5", "fa", "asset_thumbnail.webp");
        Touch("iPhone", "encoded-video", "93ca", "10", "1e", "asset.mp4");
        Touch("iPhone", "upload", "93ca", "a9", "e1", "staged.heic");
        Touch("iPhone", "backups", "immich-db-backup.sql.gz");
        Touch("2021", "holiday.jpg");

        using var db = CreateDb();
        var options = ScanOptions.Create(
        [
            Dir("iPhone", "upload"),
            Dir("iPhone", "thumbs"),
            Dir("iPhone", "encoded-video"),
            Dir("iPhone", "backups"),
            Dir("iPhone", "profile"),
        ], TimeSpan.FromMinutes(2));

        var result = await new ScanService(db, options).DiscoverAsync(_tempDir, CancellationToken.None);

        Assert.Equal(3, result.Files.Count);
        Assert.Contains(result.Files, f => f.EndsWith("IMG_5181.heic"));
        Assert.Contains(result.Files, f => f.EndsWith("IMG_5147.mov"));
        Assert.Contains(result.Files, f => f.EndsWith("holiday.jpg"));
        Assert.DoesNotContain(result.Files, f => f.Contains("thumbs"));
        Assert.DoesNotContain(result.Files, f => f.Contains("encoded-video"));
        Assert.DoesNotContain(result.Files, f => f.Contains("upload"));

        // profile/ is configured but absent from this fixture, so four of the five prune.
        Assert.Equal(4, result.PrunedDirectories);
    }
}
