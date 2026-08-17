using Api.Data;
using Api.Features.PhotoOfTheDay;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests.Features.PhotoOfTheDay;

/// <summary>
/// <see cref="PickPhotoOfTheDayHandlerTests"/> runs on the in-memory provider, which evaluates
/// everything client-side and would happily "pass" a LINQ shape Npgsql cannot translate. The
/// tier-1 <c>EXISTS</c>-over-tags is exactly the shape that broke in Phase 10, and the UTC pin
/// is invisible to the in-memory provider entirely. These render each tier through the real
/// Npgsql provider; translation happens before any connection is opened, so no database is needed.
/// </summary>
public sealed class PickPhotoOfTheDayQueryTranslationTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=translation-only;Database=none", npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options);

    public void Dispose() => _db.Dispose();

    private string PersonSql()    => PickPhotoOfTheDayHandler.BuildPersonTierQuery(_db, 8, 17, 0.5).ToQueryString();
    private string GeotaggedSql() => PickPhotoOfTheDayHandler.BuildGeotaggedTierQuery(_db, 8, 17).ToQueryString();
    private string AnySql()       => PickPhotoOfTheDayHandler.BuildAnyTierQuery(_db, 8, 17).ToQueryString();

    [Theory]
    [InlineData("person")]
    [InlineData("geotagged")]
    [InlineData("any")]
    public void EveryTier_TranslatesTheBaseFilterToSql(string tier)
    {
        var sql = tier switch
        {
            "person"    => PersonSql(),
            "geotagged" => GeotaggedSql(),
            _           => AnySql(),
        };

        // The timezone must reach SQL. Without it, date_part() on a timestamptz silently
        // resolves against whatever TimeZone the session inherited — the same trap Phase 13
        // pinned in OnThisDayHandler, and the pick must be drawn from the same set.
        Assert.Contains("AT TIME ZONE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("date_part", sql, StringComparison.OrdinalIgnoreCase);

        // sendPhoto takes a still.
        Assert.Contains("mime_type", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("is_deleted", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("origin_class", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PersonTier_TranslatesTheTagFilterToAnExists()
    {
        var sql = PersonSql();

        Assert.Contains("EXISTS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("media_tags", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confidence", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeotaggedTier_TranslatesTheCoordinateFilterToSql()
    {
        var sql = GeotaggedSql();

        Assert.Contains("latitude", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("longitude", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("media_tags", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnyTier_IsTheBareBaseFilter()
    {
        var sql = AnySql();

        Assert.DoesNotContain("media_tags", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latitude", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("person")]
    [InlineData("geotagged")]
    [InlineData("any")]
    public void EveryTier_SelectsIdsOnlyAndDoesNotOrderOrLimit(string tier)
    {
        var sql = tier switch
        {
            "person"    => PersonSql(),
            "geotagged" => GeotaggedSql(),
            _           => AnySql(),
        };

        // Ids only — the pick happens in memory, which is what sidesteps translating random().
        Assert.DoesNotContain("file_hash", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thumbnail_path", sql, StringComparison.OrdinalIgnoreCase);

        // A uniform sample: no ORDER BY, and above all no LIMIT. OnThisDayHandler's shape
        // (order by date, then cap) is why it cannot be reused here.
        Assert.DoesNotContain("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }
}
