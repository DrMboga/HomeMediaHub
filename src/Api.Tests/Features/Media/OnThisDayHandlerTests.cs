using Api.Data;
using Api.Features.Media.OnThisDay;
using Data.Entities;

namespace Api.Tests.Features.Media;

public sealed class OnThisDayHandlerTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();

    public void Dispose() => _db.Dispose();

    private static MediaFile File(
        DateTime? takenAt     = null,
        bool      isDeleted   = false,
        string?   originClass = null) => new()
    {
        Id          = Guid.NewGuid(),
        FilePath    = $"/photos/{Guid.NewGuid()}.jpg",
        FileHash    = Guid.NewGuid().ToString("N"),
        MimeType    = "image/jpeg",
        TakenAt     = takenAt,
        IsDeleted   = isDeleted,
        OriginClass = originClass,
    };

    private static DateTime At(int year, int month, int day, int hour = 12) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    private OnThisDayHandler Handler() => new(_db);

    // ── The regression guard: no cap ──────────────────────────────────────
    //
    // A hardcoded .Take(200) sat here for months and silently dropped twelve of the fifteen
    // year sections of a busy anniversary. This test seeds well past that number across
    // several years and asserts every row comes back.

    [Fact]
    public async Task Handle_MoreThan200Matches_ReturnsAllOfThem()
    {
        // 5 years × 90 photos = 450, comfortably over the old 200-row cap and close to the
        // library's real median calendar day (454).
        var years = new[] { 2025, 2024, 2023, 2022, 2021 };
        foreach (var year in years)
            for (var i = 0; i < 90; i++)
                _db.MediaFiles.Add(File(takenAt: At(year, 8, 16, hour: i % 24)));
        await _db.SaveChangesAsync();

        var groups = await Handler().Handle(new OnThisDayRequest(8, 16), default);

        Assert.Equal(5, groups.Count);
        Assert.Equal(450, groups.Sum(g => g.Items.Count));
        Assert.All(groups, g => Assert.Equal(90, g.Items.Count));
    }

    [Fact]
    public async Task Handle_GroupsAreOrderedNewestYearFirst()
    {
        foreach (var year in new[] { 2019, 2025, 2021 })
            _db.MediaFiles.Add(File(takenAt: At(year, 8, 16)));
        await _db.SaveChangesAsync();

        var groups = await Handler().Handle(new OnThisDayRequest(8, 16), default);

        Assert.Equal([2025, 2021, 2019], groups.Select(g => g.Year));
    }

    // ── Limit ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_PositiveLimit_TakesTheMostRecentMatches()
    {
        _db.MediaFiles.AddRange(
            File(takenAt: At(2025, 8, 16)),
            File(takenAt: At(2024, 8, 16)),
            File(takenAt: At(2023, 8, 16)),
            File(takenAt: At(2022, 8, 16)));
        await _db.SaveChangesAsync();

        var groups = await Handler().Handle(new OnThisDayRequest(8, 16, Limit: 2), default);

        // Newest-first selection: the two most recent years, nothing older.
        Assert.Equal([2025, 2024], groups.Select(g => g.Year));
    }

    [Fact]
    public async Task Handle_ZeroLimit_IsUnlimited()
    {
        for (var i = 0; i < 12; i++) _db.MediaFiles.Add(File(takenAt: At(2020, 8, 16, hour: i)));
        await _db.SaveChangesAsync();

        var groups = await Handler().Handle(new OnThisDayRequest(8, 16, Limit: 0), default);

        Assert.Equal(12, groups.Single().Items.Count);
    }

    // ── Selection order stays descending (load-bearing for the dashboard) ──

    [Fact]
    public async Task Handle_ItemsWithinAGroup_AreNewestFirst()
    {
        var morning = File(takenAt: At(2024, 8, 16, hour: 8));
        var noon    = File(takenAt: At(2024, 8, 16, hour: 12));
        var evening = File(takenAt: At(2024, 8, 16, hour: 20));
        _db.MediaFiles.AddRange(noon, morning, evening);
        await _db.SaveChangesAsync();

        var items = (await Handler().Handle(new OnThisDayRequest(8, 16), default)).Single().Items;

        Assert.Equal([evening.Id, noon.Id, morning.Id], items.Select(i => i.Id));
    }

    // ── Filters ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_ExcludesOtherDaysDeletedAndMemes()
    {
        var wanted = File(takenAt: At(2024, 8, 16));
        _db.MediaFiles.AddRange(
            wanted,
            File(takenAt: At(2024, 8, 17)),                        // wrong day
            File(takenAt: At(2024, 9, 16)),                        // wrong month
            File(takenAt: null),                                    // undated
            File(takenAt: At(2023, 8, 16), isDeleted: true),        // soft-deleted
            File(takenAt: At(2023, 8, 16), originClass: "meme"));   // meme
        await _db.SaveChangesAsync();

        var groups = await Handler().Handle(new OnThisDayRequest(8, 16), default);

        Assert.Equal(wanted.Id, groups.Single().Items.Single().Id);
    }

    [Fact]
    public async Task Handle_NoMatches_ReturnsEmptyList()
    {
        _db.MediaFiles.Add(File(takenAt: At(2024, 3, 3)));
        await _db.SaveChangesAsync();

        var groups = await Handler().Handle(new OnThisDayRequest(8, 16), default);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task Handle_29February_MatchesOnlyLeapYears()
    {
        _db.MediaFiles.AddRange(
            File(takenAt: At(2024, 2, 29)),
            File(takenAt: At(2020, 2, 29)),
            File(takenAt: At(2023, 2, 28)));
        await _db.SaveChangesAsync();

        var groups = await Handler().Handle(new OnThisDayRequest(2, 29), default);

        Assert.Equal([2024, 2020], groups.Select(g => g.Year));
    }

    // ── Projection ────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_ProjectsThumbnailAndAddress_AndToleratesTheirAbsence()
    {
        var withNavs = File(takenAt: At(2024, 8, 16, hour: 10));
        withNavs.Thumbnail = new MediaThumbnail { MediaFileId = withNavs.Id, ThumbnailPath = "/thumbs/a.webp" };
        withNavs.Metadata  = new MediaMetadata  { MediaFileId = withNavs.Id, Address = "Tbilisi, Georgia" };
        var bare = File(takenAt: At(2024, 8, 16, hour: 9));
        _db.MediaFiles.AddRange(withNavs, bare);
        await _db.SaveChangesAsync();

        var items = (await Handler().Handle(new OnThisDayRequest(8, 16), default)).Single().Items;

        var a = items.Single(i => i.Id == withNavs.Id);
        Assert.Equal("/thumbs/a.webp", a.ThumbnailPath);
        Assert.Equal("Tbilisi, Georgia", a.Address);

        var b = items.Single(i => i.Id == bare.Id);
        Assert.Null(b.ThumbnailPath);
        Assert.Null(b.Address);
    }
}
