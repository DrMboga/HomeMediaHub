using Api.Data;
using Api.Features.Tags;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests.Features.Tags;

/// <summary>
/// The other tag tests run on the in-memory provider, which evaluates everything client-side —
/// it would happily "pass" a LINQ shape Npgsql cannot translate, and the failure would surface
/// only as a 500 on stage. These render the same queries to SQL through the real Npgsql
/// provider. Translation happens before any connection is opened, so no database is needed.
/// </summary>
public sealed class TagQueryTranslationTests : IDisposable
{
    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=translation-only;Database=none", npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void TagCounts_NoSelection_TranslatesToCountDistinctOrderedInSql()
    {
        var sql = GetTagsHandler.BuildQuery(_db, []).ToQueryString();

        // Photos, not tag rows.
        Assert.Contains("count(DISTINCT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        // The ordering must reach SQL. If the record projection is moved ahead of the
        // OrderBy, EF stops being able to translate it at all — this pins the shape.
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TagCounts_WithSelection_Translates(int selectedCount)
    {
        var selected = Enumerable.Range(0, selectedCount).Select(i => $"tag{i}").ToArray();

        var sql = GetTagsHandler.BuildQuery(_db, selected).ToQueryString();

        // One EXISTS per selected tag — the AND chain, not an IN list.
        Assert.Equal(selectedCount, CountOccurrences(sql, "EXISTS ("));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FilesCarryingAllTags_Translates(int tagCount)
    {
        var tags = Enumerable.Range(0, tagCount).Select(i => $"tag{i}").ToArray();

        var sql = TagQueries.FilesCarryingAllTags(_db, tags)
            .Include(f => f.Thumbnail)
            .Include(f => f.Metadata)
            .OrderByDescending(f => f.TakenAt)
            .ThenByDescending(f => f.Id)
            .Skip(0)
            .Take(50)
            .ToQueryString();

        Assert.Equal(tagCount, CountOccurrences(sql, "EXISTS ("));
        Assert.Contains("media_tags", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.OrdinalIgnoreCase))
            count++;
        return count;
    }
}
