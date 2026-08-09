using Api.Data;
using Api.Features.Media;
using Data.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Tags;

// ── DTOs ────────────────────────────────────────────────────────────────────

/// <summary>A tag and how many photos carry it, within whatever selection is active.</summary>
public record TagCountDto(string Tag, int Count);

// ── Requests ────────────────────────────────────────────────────────────────

/// <summary>
/// The full tag vocabulary when <paramref name="Selected"/> is empty; otherwise the counts
/// that remain <i>alongside</i> the current selection. A tag absent from the response
/// co-occurs with nothing selected, which is what greys its chip out.
/// </summary>
public record GetTagsRequest(IReadOnlyList<string> Selected) : IRequest<IReadOnlyList<TagCountDto>>;

public record GetTagMediaRequest(IReadOnlyList<string> Tags, int Page, int PageSize)
    : IRequest<PagedResult<MediaListItemDto>>;

// ── Shared query ────────────────────────────────────────────────────────────

internal static class TagQueries
{
    /// <summary>
    /// Media carrying <b>all</b> of <paramref name="tags"/> (AND, not OR), excluding
    /// soft-deleted and memes — matching Gallery, semantic search and On This Day.
    /// </summary>
    /// <remarks>
    /// One <c>EXISTS</c> per tag rather than the <c>GROUP BY … HAVING COUNT(DISTINCT tag)</c>
    /// the delivery plan sketched. Same result, and it needs no DISTINCT to be safe — a
    /// duplicate <c>media_tags</c> row cannot change whether a row exists. That matters
    /// because <c>media_tags</c> carries only a non-unique index on <c>Tag</c>
    /// (MediaModelConfiguration.cs:67), so nothing at the schema level forbids duplicates.
    /// Each EXISTS is an index lookup on that column, and selections run a few chips deep.
    /// Composable too, which the HAVING form is not: the same builder backs both the facet
    /// counts and the paged grid.
    /// </remarks>
    internal static IQueryable<MediaFile> FilesCarryingAllTags(AppDbContext db, IReadOnlyList<string> tags)
    {
        var q = db.MediaFiles.Where(f => !f.IsDeleted && f.OriginClass != "meme");

        foreach (var tag in tags)
            q = q.Where(f => f.Tags.Any(t => t.Tag == tag));

        return q;
    }
}

// ── Handlers ────────────────────────────────────────────────────────────────

public sealed class GetTagsHandler : IRequestHandler<GetTagsRequest, IReadOnlyList<TagCountDto>>
{
    private readonly AppDbContext _db;
    public GetTagsHandler(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<TagCountDto>> Handle(GetTagsRequest req, CancellationToken ct) =>
        await BuildQuery(_db, req.Selected).ToListAsync(ct);

    // Exposed so a test can render it to SQL against the real Npgsql provider — the
    // in-memory provider evaluates client-side and would not catch a translation failure.
    internal static IQueryable<TagCountDto> BuildQuery(AppDbContext db, IReadOnlyList<string> selected)
    {
        var fileIds = TagQueries.FilesCarryingAllTags(db, selected).Select(f => f.Id);

        // COUNT(DISTINCT media_file_id): photos, not tag rows. See FilesCarryingAllTags on
        // why duplicate (media_file_id, tag) rows are not structurally ruled out.
        //
        // The aggregate lands in an anonymous type and only becomes a TagCountDto after the
        // ordering — that shape is load-bearing, not stylistic. EF Core 10 cannot order by a
        // property of a positional record it just constructed (it fails to map `x.Count` back
        // to the aggregate and throws "could not be translated"), while the same ordering over
        // an anonymous projection translates to ORDER BY count(DISTINCT …) and the record
        // projection tacked on last translates away entirely.
        return db.MediaTags
            .Where(t => fileIds.Contains(t.MediaFileId))
            .GroupBy(t => t.Tag)
            .Select(g => new { Tag = g.Key, Count = g.Select(x => x.MediaFileId).Distinct().Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Tag)
            .Select(x => new TagCountDto(x.Tag, x.Count));
    }
}

public sealed class GetTagMediaHandler : IRequestHandler<GetTagMediaRequest, PagedResult<MediaListItemDto>>
{
    private readonly AppDbContext _db;
    public GetTagMediaHandler(AppDbContext db) => _db = db;

    public async Task<PagedResult<MediaListItemDto>> Handle(GetTagMediaRequest req, CancellationToken ct)
    {
        // No tags selected is a legitimate state (the page loads that way), not an error —
        // it just means "no filter applied yet", so return nothing rather than the library.
        if (req.Tags.Count == 0)
            return new PagedResult<MediaListItemDto>([], 0, req.Page, req.PageSize);

        var q = TagQueries.FilesCarryingAllTags(_db, req.Tags);

        var total = await q.CountAsync(ct);

        var items = await q
            .Include(f => f.Thumbnail)
            .Include(f => f.Metadata)
            // Newest first, tie-broken by Id so paging is stable — matches Gallery.
            .OrderByDescending(f => f.TakenAt)
            .ThenByDescending(f => f.Id)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .ToListAsync(ct);

        return new PagedResult<MediaListItemDto>(
            items.Select(f => f.ToListItemDto()).ToList(),
            total, req.Page, req.PageSize);
    }
}
