using Api.Data;
using Api.Features.PhotoOfTheDay;
using Data.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Tests.Features.PhotoOfTheDay;

public sealed class PickPhotoOfTheDayHandlerTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();

    public void Dispose() => _db.Dispose();

    private PickPhotoOfTheDayHandler Handler(double? minConfidence = null)
    {
        var settings = new Dictionary<string, string?>();
        if (minConfidence is { } c) settings["Potd:MinConfidence"] = c.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new PickPhotoOfTheDayHandler(_db, config, NullLogger<PickPhotoOfTheDayHandler>.Instance);
    }

    private static DateTime At(int year, int month, int day, int hour = 12) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Adds a file and returns its id, wiring up whichever of the three tier signals were asked
    /// for. <paramref name="takenAt"/> defaults to 17 Aug 2023 — the day every test picks for —
    /// so <paramref name="undated"/> is a separate flag rather than "pass null".
    /// </summary>
    private Guid Add(
        DateTime? takenAt      = null,
        bool      undated      = false,
        string    mimeType     = "image/jpeg",
        bool      isDeleted    = false,
        string?   originClass  = null,
        float?    personTag    = null,
        bool      geotagged    = false)
    {
        var id = Guid.NewGuid();

        var file = new MediaFile
        {
            Id          = id,
            FilePath    = $"/photos/{id}.jpg",
            FileHash    = id.ToString("N"),
            MimeType    = mimeType,
            TakenAt     = undated ? null : takenAt ?? At(2023, 8, 17),
            IsDeleted   = isDeleted,
            OriginClass = originClass,
        };

        if (personTag is { } confidence)
            file.Tags.Add(new MediaTag { Id = Guid.NewGuid(), MediaFileId = id, Tag = "person", Confidence = confidence, Model = "yolov8n" });

        _db.MediaFiles.Add(file);

        if (geotagged)
            _db.MediaMetadata.Add(new MediaMetadata { MediaFileId = id, Latitude = 52.52, Longitude = 13.405 });

        return id;
    }

    // ── Tier fallback order ───────────────────────────────────────────────
    //
    // The whole point of the three tiers is that something goes out every day. These pin the
    // order and, just as importantly, that the random pick happens *inside* the matching tier —
    // a geotagged photo must never compete with a person photo.

    [Fact]
    public async Task Handle_DayWithPersonPhotos_PicksFromTierOne()
    {
        var person = Add(personTag: 0.9f);
        Add(geotagged: true);
        Add();
        await _db.SaveChangesAsync();

        var pick = await Handler().Handle(new PickPhotoOfTheDayRequest(8, 17), default);

        Assert.NotNull(pick);
        Assert.Equal(PotdTier.Person, pick.Tier);
        Assert.Equal(person, pick.MediaFileId);
        Assert.Equal(1, pick.CandidateCount);
    }

    [Fact]
    public async Task Handle_DayWithNoPersonPhotos_FallsBackToGeotagged()
    {
        var geo = Add(geotagged: true);
        Add();
        await _db.SaveChangesAsync();

        var pick = await Handler().Handle(new PickPhotoOfTheDayRequest(8, 17), default);

        Assert.NotNull(pick);
        Assert.Equal(PotdTier.Geotagged, pick.Tier);
        Assert.Equal(geo, pick.MediaFileId);
    }

    [Fact]
    public async Task Handle_DayWithNeitherSignal_FallsBackToAnyNonVideo()
    {
        var plain = Add();
        await _db.SaveChangesAsync();

        var pick = await Handler().Handle(new PickPhotoOfTheDayRequest(8, 17), default);

        Assert.NotNull(pick);
        Assert.Equal(PotdTier.Any, pick.Tier);
        Assert.Equal(plain, pick.MediaFileId);
    }

    [Fact]
    public async Task Handle_EmptyDay_ReturnsNull()
    {
        Add(takenAt: At(2023, 5, 2), personTag: 0.9f);
        await _db.SaveChangesAsync();

        Assert.Null(await Handler().Handle(new PickPhotoOfTheDayRequest(8, 17), default));
    }

    // ── The tier-1 confidence floor ───────────────────────────────────────

    [Fact]
    public async Task Handle_PersonTagBelowTheFloor_DoesNotSatisfyTierOne()
    {
        // 0.4 is under the 0.5 default. The photo is still a candidate — via tier 3.
        var marginal = Add(personTag: 0.4f);
        await _db.SaveChangesAsync();

        var pick = await Handler().Handle(new PickPhotoOfTheDayRequest(8, 17), default);

        Assert.NotNull(pick);
        Assert.Equal(PotdTier.Any, pick.Tier);
        Assert.Equal(marginal, pick.MediaFileId);
    }

    [Fact]
    public async Task Handle_ConfiguredFloorOverridesTheDefault()
    {
        Add(personTag: 0.6f);
        await _db.SaveChangesAsync();

        // 0.6 clears the 0.5 default but not a configured 0.8.
        Assert.Equal(PotdTier.Person, (await Handler().Handle(new PickPhotoOfTheDayRequest(8, 17), default))!.Tier);
        Assert.Equal(PotdTier.Any, (await Handler(minConfidence: 0.8).Handle(new PickPhotoOfTheDayRequest(8, 17), default))!.Tier);
    }

    // ── The base filter ───────────────────────────────────────────────────

    [Fact]
    public async Task Handle_ExcludesVideosFromEveryTier()
    {
        // sendPhoto takes a still. A video with a person tag must not win tier 1, and must not
        // be the answer in tier 3 either.
        Add(mimeType: "video/quicktime", personTag: 0.95f);
        await _db.SaveChangesAsync();

        Assert.Null(await Handler().Handle(new PickPhotoOfTheDayRequest(8, 17), default));
    }

    [Fact]
    public async Task Handle_ExcludesDeletedAndMemes()
    {
        Add(isDeleted: true, personTag: 0.9f);
        Add(originClass: "meme", personTag: 0.9f);
        await _db.SaveChangesAsync();

        Assert.Null(await Handler().Handle(new PickPhotoOfTheDayRequest(8, 17), default));
    }

    [Fact]
    public async Task Handle_ExcludesOtherCalendarDaysButSpansAllYears()
    {
        var ids = new[] { 2015, 2019, 2024 }.Select(y => Add(takenAt: At(y, 8, 17))).ToHashSet();
        Add(takenAt: At(2024, 8, 18));  // day before/after must not leak in
        Add(takenAt: At(2024, 7, 17));
        await _db.SaveChangesAsync();

        var pick = await Handler().Handle(new PickPhotoOfTheDayRequest(8, 17), default);

        Assert.NotNull(pick);
        Assert.Equal(3, pick.CandidateCount);
        Assert.Contains(pick.MediaFileId, ids);
    }

    [Fact]
    public async Task Handle_ExcludesUndatedFiles()
    {
        Add(undated: true);
        await _db.SaveChangesAsync();

        Assert.Null(await Handler().Handle(new PickPhotoOfTheDayRequest(8, 17), default));
    }

    [Fact]
    public async Task Handle_TwentyNinthOfFebruary_MatchesOnlyLeapYears()
    {
        Add(takenAt: At(2024, 2, 29));
        Add(takenAt: At(2023, 2, 28));
        await _db.SaveChangesAsync();

        var pick = await Handler().Handle(new PickPhotoOfTheDayRequest(2, 29), default);

        Assert.NotNull(pick);
        Assert.Equal(1, pick.CandidateCount);
    }

    // ── The random pick itself ────────────────────────────────────────────

    [Fact]
    public async Task Handle_CanPickTheLastCandidate()
    {
        // The old Pi implementation used Next(0, len - 1), which could never return the last
        // element. Next(0, count) has an exclusive upper bound, so every index is reachable.
        var ids = Enumerable.Range(0, 6).Select(i => Add(takenAt: At(2010 + i, 8, 17), personTag: 0.9f)).ToList();
        await _db.SaveChangesAsync();

        var handler = Handler();
        var seen = new HashSet<Guid>();
        for (var i = 0; i < 400; i++)
            seen.Add((await handler.Handle(new PickPhotoOfTheDayRequest(8, 17), default))!.MediaFileId);

        // P(missing any given candidate over 400 draws from 6) is (5/6)^400 ≈ 1e-32.
        Assert.Equal(ids.ToHashSet(), seen);
    }
}
