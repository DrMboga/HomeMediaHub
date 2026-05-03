using Api.Data;
using Api.Features.Media.GetMediaList;

namespace Api.Tests.Features.Media;

public sealed class GetMediaListHandlerTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();

    public void Dispose() => _db.Dispose();

    private static MediaFile File(
        DateTime? takenAt    = null,
        bool      isDeleted  = false,
        bool      isFavorite = false) => new()
    {
        Id         = Guid.NewGuid(),
        FilePath   = $"/photos/{Guid.NewGuid()}.jpg",
        FileHash   = Guid.NewGuid().ToString("N"),
        MimeType   = "image/jpeg",
        TakenAt    = takenAt,
        IsDeleted  = isDeleted,
        IsFavorite = isFavorite,
    };

    private GetMediaListHandler Handler() => new(_db);

    // ── Soft-delete ───────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_DeletedFiles_AreExcluded()
    {
        _db.MediaFiles.AddRange(File(), File(), File(isDeleted: true));
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(new GetMediaListRequest(1, 50, null, null, null, null), default);

        Assert.Equal(2, result.Total);
    }

    // ── Date filters ──────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_FromFilter_ExcludesOlderFiles()
    {
        var old    = File(takenAt: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var recent = File(takenAt: new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        _db.MediaFiles.AddRange(old, recent);
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(
            new GetMediaListRequest(1, 50, new DateTime(2010, 1, 1), null, null, null), default);

        Assert.Equal(1, result.Total);
        Assert.Equal(recent.Id, result.Items[0].Id);
    }

    [Fact]
    public async Task Handle_ToFilter_ExcludesNewerFiles()
    {
        var old    = File(takenAt: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var recent = File(takenAt: new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        _db.MediaFiles.AddRange(old, recent);
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(
            new GetMediaListRequest(1, 50, null, new DateTime(2010, 1, 1), null, null), default);

        Assert.Equal(1, result.Total);
        Assert.Equal(old.Id, result.Items[0].Id);
    }

    [Fact]
    public async Task Handle_NullTakenAt_IsExcludedByDateFilter()
    {
        _db.MediaFiles.AddRange(
            File(takenAt: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            File(takenAt: null));
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(
            new GetMediaListRequest(1, 50, new DateTime(2019, 1, 1), null, null, null), default);

        Assert.Equal(1, result.Total);
    }

    // ── Favorite filter ───────────────────────────────────────────────────

    [Fact]
    public async Task Handle_FavoriteFilter_ReturnsOnlyFavorites()
    {
        _db.MediaFiles.AddRange(File(isFavorite: true), File(), File());
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(
            new GetMediaListRequest(1, 50, null, null, null, true), default);

        Assert.Equal(1, result.Total);
        Assert.True(result.Items[0].IsFavorite);
    }

    [Fact]
    public async Task Handle_FavoriteFalseFilter_ReturnsOnlyNonFavorites()
    {
        _db.MediaFiles.AddRange(File(isFavorite: true), File(), File());
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(
            new GetMediaListRequest(1, 50, null, null, null, false), default);

        Assert.Equal(2, result.Total);
        Assert.All(result.Items, item => Assert.False(item.IsFavorite));
    }

    // ── Pagination ────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_Pagination_ReturnsCorrectPageAndTotal()
    {
        for (var i = 0; i < 10; i++) _db.MediaFiles.Add(File());
        await _db.SaveChangesAsync();

        var page1 = await Handler().Handle(new GetMediaListRequest(1, 4, null, null, null, null), default);
        var page2 = await Handler().Handle(new GetMediaListRequest(2, 4, null, null, null, null), default);

        Assert.Equal(10, page1.Total);
        Assert.Equal(4,  page1.Items.Count);
        Assert.Equal(4,  page2.Items.Count);
        Assert.Empty(page1.Items.Select(x => x.Id).Intersect(page2.Items.Select(x => x.Id)));
    }

    [Fact]
    public async Task Handle_LastPage_ReturnsRemainingItems()
    {
        for (var i = 0; i < 7; i++) _db.MediaFiles.Add(File());
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(new GetMediaListRequest(2, 4, null, null, null, null), default);

        Assert.Equal(7, result.Total);
        Assert.Equal(3, result.Items.Count);
    }

    // ── Ordering ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_ResultsOrderedByTakenAtDescending()
    {
        var oldest  = File(takenAt: new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var middle  = File(takenAt: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest  = File(takenAt: new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _db.MediaFiles.AddRange(oldest, newest, middle);
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(new GetMediaListRequest(1, 50, null, null, null, null), default);

        Assert.Equal(newest.Id, result.Items[0].Id);
        Assert.Equal(middle.Id, result.Items[1].Id);
        Assert.Equal(oldest.Id, result.Items[2].Id);
    }
}
