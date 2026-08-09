using Api.Data;
using Api.Features.Tags;
using Data.Entities;

namespace Api.Tests.Features.Tags;

public sealed class TagHandlersTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();

    public void Dispose() => _db.Dispose();

    private MediaFile File(string? originClass = null, bool isDeleted = false,
                           DateTime? takenAt = null, params string[] tags)
    {
        var f = new MediaFile
        {
            Id          = Guid.NewGuid(),
            FilePath    = $"/photos/{Guid.NewGuid()}.jpg",
            FileHash    = Guid.NewGuid().ToString("N"),
            MimeType    = "image/jpeg",
            OriginClass = originClass,
            IsDeleted   = isDeleted,
            TakenAt     = takenAt,
        };
        _db.MediaFiles.Add(f);
        foreach (var tag in tags)
            _db.MediaTags.Add(new MediaTag
            {
                Id = Guid.NewGuid(), MediaFileId = f.Id, Tag = tag, Confidence = 0.9f, Model = "yolov8n",
            });
        return f;
    }

    private GetTagsHandler     Tags()  => new(_db);
    private GetTagMediaHandler Media() => new(_db);

    private static GetTagsRequest     TagsFor(params string[] selected) => new(selected);
    private static GetTagMediaRequest MediaFor(params string[] tags)    => new(tags, 1, 50);

    // ── Vocabulary ────────────────────────────────────────────────────────

    [Fact]
    public async Task Tags_NoSelection_ReturnsWholeVocabularyByPhotoCount()
    {
        File(tags: ["person", "car"]);
        File(tags: ["person"]);
        File(tags: ["boat"]);
        await _db.SaveChangesAsync();

        var result = await Tags().Handle(TagsFor(), default);

        Assert.Equal(3, result.Count);
        Assert.Equal(new TagCountDto("person", 2), result[0]);
        // count desc, then tag asc — boat before car at the same count
        Assert.Equal(new TagCountDto("boat", 1), result[1]);
        Assert.Equal(new TagCountDto("car", 1),  result[2]);
    }

    [Fact]
    public async Task Tags_CountsPhotosNotTagRows()
    {
        // A duplicate (media_file_id, tag) row is not forbidden by the schema — only a
        // non-unique index on Tag exists — so the count must be DISTINCT over media_file_id.
        var f = File(tags: ["car"]);
        _db.MediaTags.Add(new MediaTag
        {
            Id = Guid.NewGuid(), MediaFileId = f.Id, Tag = "car", Confidence = 0.8f, Model = "yolov8n",
        });
        await _db.SaveChangesAsync();

        var result = await Tags().Handle(TagsFor(), default);

        Assert.Equal(new TagCountDto("car", 1), Assert.Single(result));
    }

    [Fact]
    public async Task Media_DuplicateTagRow_DoesNotDuplicateThePhoto()
    {
        // Nothing at the schema level forbids a second (media_file_id, tag) row — media_tags
        // has only a non-unique index on Tag. The pipeline never writes one, but the grid must
        // not double-count if it ever did, which is why the AND filter is EXISTS and not a join.
        var f = File(tags: ["car"]);
        _db.MediaTags.Add(new MediaTag
        {
            Id = Guid.NewGuid(), MediaFileId = f.Id, Tag = "car", Confidence = 0.8f, Model = "yolov8n",
        });
        await _db.SaveChangesAsync();

        var result = await Media().Handle(MediaFor("car"), default);

        Assert.Equal(1, result.Total);
        Assert.Equal(f.Id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task Tags_ExcludesDeletedAndMemes()
    {
        File(tags: ["car"]);
        File(isDeleted: true, tags: ["car", "boat"]);
        File(originClass: "meme", tags: ["car", "person"]);
        await _db.SaveChangesAsync();

        var result = await Tags().Handle(TagsFor(), default);

        Assert.Equal(new TagCountDto("car", 1), Assert.Single(result));
    }

    // ── Facet narrowing ───────────────────────────────────────────────────

    [Fact]
    public async Task Tags_WithSelection_ReturnsOnlyCoOccurringTags()
    {
        File(tags: ["person", "bottle"]);
        File(tags: ["person", "car"]);
        File(tags: ["boat"]);          // shares nothing with person
        await _db.SaveChangesAsync();

        var result = await Tags().Handle(TagsFor("person"), default);
        var byTag  = result.ToDictionary(t => t.Tag, t => t.Count);

        // The selected tag reports its own count; boat is absent, which is what greys it out.
        Assert.Equal(2, byTag["person"]);
        Assert.Equal(1, byTag["bottle"]);
        Assert.Equal(1, byTag["car"]);
        Assert.DoesNotContain("boat", byTag.Keys);
    }

    [Fact]
    public async Task Tags_MultipleSelected_NarrowsByAnd()
    {
        File(tags: ["person", "bottle", "cup"]);
        File(tags: ["person", "bottle"]);
        File(tags: ["person", "car"]);
        await _db.SaveChangesAsync();

        var byTag = (await Tags().Handle(TagsFor("person", "bottle"), default))
            .ToDictionary(t => t.Tag, t => t.Count);

        Assert.Equal(2, byTag["person"]);
        Assert.Equal(2, byTag["bottle"]);
        Assert.Equal(1, byTag["cup"]);
        // car only co-occurs with person, never with bottle — a dead end, so absent.
        Assert.DoesNotContain("car", byTag.Keys);
    }

    // ── Media grid ────────────────────────────────────────────────────────

    [Fact]
    public async Task Media_SingleTag_ReturnsCarriers()
    {
        var wanted = File(tags: ["car"]);
        File(tags: ["boat"]);
        await _db.SaveChangesAsync();

        var result = await Media().Handle(MediaFor("car"), default);

        Assert.Equal(1, result.Total);
        Assert.Equal(wanted.Id, result.Items[0].Id);
    }

    [Fact]
    public async Task Media_MultipleTags_RequiresAllOfThem()
    {
        var both = File(tags: ["car", "person"]);
        File(tags: ["car"]);      // OR would wrongly include these two
        File(tags: ["person"]);
        await _db.SaveChangesAsync();

        var result = await Media().Handle(MediaFor("car", "person"), default);

        Assert.Equal(1, result.Total);
        Assert.Equal(both.Id, result.Items[0].Id);
    }

    [Fact]
    public async Task Media_ExcludesDeletedAndMemes()
    {
        var live = File(tags: ["car"]);
        File(isDeleted: true, tags: ["car"]);
        File(originClass: "meme", tags: ["car"]);
        await _db.SaveChangesAsync();

        var result = await Media().Handle(MediaFor("car"), default);

        Assert.Equal(1, result.Total);
        Assert.Equal(live.Id, result.Items[0].Id);
    }

    [Fact]
    public async Task Media_NoTags_ReturnsEmptyRatherThanWholeLibrary()
    {
        File(tags: ["car"]);
        File(tags: ["boat"]);
        await _db.SaveChangesAsync();

        var result = await Media().Handle(MediaFor(), default);

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Media_OrderedByTakenAtDescending()
    {
        var oldest = File(takenAt: new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc), tags: ["car"]);
        var newest = File(takenAt: new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), tags: ["car"]);
        var middle = File(takenAt: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), tags: ["car"]);
        await _db.SaveChangesAsync();

        var result = await Media().Handle(MediaFor("car"), default);

        Assert.Equal([newest.Id, middle.Id, oldest.Id], result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Media_Pagination_SplitsWithoutOverlap()
    {
        for (var i = 0; i < 7; i++)
            File(takenAt: new DateTime(2020, 1, i + 1, 0, 0, 0, DateTimeKind.Utc), tags: ["car"]);
        await _db.SaveChangesAsync();

        var page1 = await Media().Handle(new GetTagMediaRequest(["car"], 1, 4), default);
        var page2 = await Media().Handle(new GetTagMediaRequest(["car"], 2, 4), default);

        Assert.Equal(7, page1.Total);
        Assert.Equal(4, page1.Items.Count);
        Assert.Equal(3, page2.Items.Count);
        Assert.Empty(page1.Items.Select(x => x.Id).Intersect(page2.Items.Select(x => x.Id)));
    }

    [Fact]
    public async Task Media_UnknownTag_ReturnsEmpty()
    {
        File(tags: ["car"]);
        await _db.SaveChangesAsync();

        var result = await Media().Handle(MediaFor("dinosaur"), default);

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }
}
