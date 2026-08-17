using Api.Data;
using Api.Features.Media.OnThisDay;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests.Features.Media;

/// <summary>
/// <see cref="OnThisDayHandlerTests"/> runs on the in-memory provider, which evaluates
/// everything client-side and would happily "pass" a LINQ shape Npgsql cannot translate —
/// including the UTC pin, which is the whole point of the rewrite. These render the query
/// through the real Npgsql provider; translation happens before any connection is opened,
/// so no database is needed. Same reasoning as <c>TagQueryTranslationTests</c>.
/// </summary>
public sealed class OnThisDayQueryTranslationTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=translation-only;Database=none", npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Query_PinsTheMonthDayBoundaryToUtcInSql()
    {
        var sql = OnThisDayHandler.BuildQuery(_db, 8, 16, 0).ToQueryString();

        // The timezone must reach SQL. Without it, date_part() silently resolves against
        // whatever TimeZone the session inherited.
        Assert.Contains("AT TIME ZONE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("date_part", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Query_WithoutLimit_EmitsNoLimitClause()
    {
        var sql = OnThisDayHandler.BuildQuery(_db, 8, 16, 0).ToQueryString();

        Assert.DoesNotContain("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Query_WithLimit_EmitsLimitClause()
    {
        var sql = OnThisDayHandler.BuildQuery(_db, 8, 16, 200).ToQueryString();

        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Query_ProjectsInsteadOfLoadingEntities()
    {
        var sql = OnThisDayHandler.BuildQuery(_db, 8, 16, 0).ToQueryString();

        // The two navigations come in as LEFT JOINs on the projected columns, not as
        // Include()'d entity graphs — this is what keeps a 2,250-row day cheap.
        Assert.Contains("LEFT JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thumbnail_path", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("address", sql, StringComparison.OrdinalIgnoreCase);
        // file_hash / file_path are entity columns nothing in the DTO needs.
        Assert.DoesNotContain("file_hash", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Query_SelectsNewestFirst()
    {
        var sql = OnThisDayHandler.BuildQuery(_db, 8, 16, 0).ToQueryString();

        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", sql, StringComparison.OrdinalIgnoreCase);
    }
}
