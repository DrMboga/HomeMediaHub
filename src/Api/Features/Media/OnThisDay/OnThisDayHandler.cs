using Api.Data;
using Api.Features.Media;
using Data.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.OnThisDay;

/// <param name="Limit">
/// Applied only when positive. Default is unlimited on purpose: a caller that forgets a limit
/// gets a slow page, which is obvious, rather than a silently truncated one — a hardcoded
/// <c>Take(200)</c> hid twelve years of every anniversary here for months.
/// </param>
public record OnThisDayRequest(int Month, int Day, int Limit = 0) : IRequest<List<OtdGroupDto>>;

public record OtdGroupDto(int Year, List<MediaListItemDto> Items);

public sealed class OnThisDayHandler : IRequestHandler<OnThisDayRequest, List<OtdGroupDto>>
{
    private readonly AppDbContext _db;
    public OnThisDayHandler(AppDbContext db) => _db = db;

    public async Task<List<OtdGroupDto>> Handle(OnThisDayRequest req, CancellationToken ct)
    {
        var items = await BuildQuery(_db, req.Month, req.Day, req.Limit).ToListAsync(ct);

        // Grouping stays client-side: it runs over the already-materialised list and is cheap
        // even at the worst calendar day in the library (~2,250 rows).
        return items
            .GroupBy(i => i.TakenAt!.Value.Year)
            .OrderByDescending(g => g.Key)
            .Select(g => new OtdGroupDto(g.Key, g.ToList()))
            .ToList();
    }

    /// <summary>
    /// Newest-first is the <em>selection</em> order, not the display order — it is what makes
    /// <paramref name="limit"/> mean "the most recent N" for the dashboard strip. The On This Day
    /// page sorts ascending within each year block itself.
    /// </summary>
    internal static IQueryable<MediaListItemDto> BuildQuery(AppDbContext db, int month, int day, int limit)
    {
        // taken_at is timestamptz, so date_part() resolves against the *session* TimeZone.
        // Pin it to UTC so the month/day boundary is a property of this query rather than
        // something inherited from the connection — the date picker makes that boundary
        // user-visible (a 23:30 local photo can land on the neighbouring day).
        IQueryable<MediaFile> q = db.MediaFiles
            .Where(f => !f.IsDeleted &&
                        f.OriginClass != "meme" &&
                        f.TakenAt.HasValue &&
                        TimeZoneInfo.ConvertTimeBySystemTimeZoneId(f.TakenAt!.Value, "UTC").Month == month &&
                        TimeZoneInfo.ConvertTimeBySystemTimeZoneId(f.TakenAt!.Value, "UTC").Day == day)
            .OrderByDescending(f => f.TakenAt);

        if (limit > 0) q = q.Take(limit);

        // Project rather than Include: at 2,250 rows, materialising full entities with two
        // navigations each costs far more than the four columns actually rendered.
        return q.Select(f => new MediaListItemDto(
            f.Id,
            f.MimeType,
            f.Thumbnail!.ThumbnailPath,
            f.TakenAt,
            f.IsFavorite,
            f.DurationSec.HasValue,
            f.OriginClass,
            f.OriginConfidence,
            f.Metadata!.Address));
    }
}
