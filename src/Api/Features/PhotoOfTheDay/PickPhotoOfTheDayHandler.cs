using Api.Data;
using Data.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.PhotoOfTheDay;

/// <summary>Which fallback tier supplied the photo. Logged and returned — see <see cref="PickPhotoOfTheDayHandler"/>.</summary>
public static class PotdTier
{
    public const string Person    = "person";
    public const string Geotagged = "geotagged";
    public const string Any       = "any";
    /// <summary>Not a tier — the caller passed an explicit mediaFileId and the pick was skipped.</summary>
    public const string Explicit  = "explicit";
}

public record PickPhotoOfTheDayRequest(int Month, int Day) : IRequest<PhotoOfTheDayPick?>;

public record PhotoOfTheDayPick(Guid MediaFileId, string Tier, int CandidateCount);

/// <summary>
/// Picks one photo at random from the given calendar day across all years, falling through three
/// tiers so something goes out every day (architecture.md §14).
/// </summary>
/// <remarks>
/// <para>
/// The random choice happens <b>inside whichever tier matched</b>, not across the union — a
/// geotagged photo never competes with a person photo.
/// </para>
/// <para>
/// This is a sibling of <c>OnThisDayHandler</c>, not an extension of it: that one has no tag
/// filter, includes videos, and orders by date, so it is not a uniform sample. <c>/on-this-day</c>
/// depends on its current shape.
/// </para>
/// </remarks>
public sealed class PickPhotoOfTheDayHandler : IRequestHandler<PickPhotoOfTheDayRequest, PhotoOfTheDayPick?>
{
    /// <summary>
    /// Tier 1 only. A daily single-photo pick is a precision problem, not a recall one — the
    /// opposite of Phase 10's tag browsing, which rejected a floor because it thinned a starved
    /// dataset. Measured on prod 2026-08-17: at 0.5 every one of the 366 calendar days has at
    /// least 31 candidates (mean 281), so tiers 2 and 3 never fire on today's library.
    /// </summary>
    public const double DefaultMinConfidence = 0.5;

    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<PickPhotoOfTheDayHandler> _log;

    public PickPhotoOfTheDayHandler(AppDbContext db, IConfiguration config, ILogger<PickPhotoOfTheDayHandler> log)
    {
        _db     = db;
        _config = config;
        _log    = log;
    }

    public async Task<PhotoOfTheDayPick?> Handle(PickPhotoOfTheDayRequest req, CancellationToken ct)
    {
        var minConfidence = _config.GetValue("Potd:MinConfidence", DefaultMinConfidence);

        // Lazily — tier 2 only runs if tier 1 came back empty. Three round trips worst case,
        // once a day, against a day-sized slice.
        foreach (var (tier, query) in new (string, IQueryable<Guid>)[]
        {
            (PotdTier.Person,    BuildPersonTierQuery(_db, req.Month, req.Day, minConfidence)),
            (PotdTier.Geotagged, BuildGeotaggedTierQuery(_db, req.Month, req.Day)),
            (PotdTier.Any,       BuildAnyTierQuery(_db, req.Month, req.Day)),
        })
        {
            // Ids only, then pick in memory: sidesteps translating random(), and a calendar
            // day's slice is small even at archive scale (max 2,178 on prod).
            var ids = await query.ToListAsync(ct);
            if (ids.Count == 0) continue;

            // Next(0, count) — exclusive upper bound. The old Pi implementation's
            // Next(0, len - 1) could never pick the last element.
            var pick = ids[Random.Shared.Next(0, ids.Count)];

            _log.LogInformation(
                "Photo of the Day {Month:00}-{Day:00}: tier={Tier}, {Count} candidates, picked {MediaFileId}",
                req.Month, req.Day, tier, ids.Count, pick);

            return new PhotoOfTheDayPick(pick, tier, ids.Count);
        }

        _log.LogWarning("Photo of the Day {Month:00}-{Day:00}: no postable photo in any tier", req.Month, req.Day);
        return null;
    }

    /// <summary>
    /// The On This Day set minus what cannot be posted. <c>sendPhoto</c> takes a still, so videos
    /// are out; memes are out for the same reason they are out of the gallery default.
    /// </summary>
    /// <remarks>
    /// The month/day boundary is pinned to UTC exactly as <c>OnThisDayHandler</c> pins it, so the
    /// pick is drawn from the same set the <c>/on-this-day</c> page shows. Without the pin,
    /// <c>date_part</c> on a <c>timestamptz</c> resolves against the session TimeZone.
    /// </remarks>
    internal static IQueryable<MediaFile> BuildBaseQuery(AppDbContext db, int month, int day) =>
        db.MediaFiles.Where(f =>
            !f.IsDeleted &&
            f.OriginClass != "meme" &&
            !f.MimeType.StartsWith("video/") &&
            f.TakenAt.HasValue &&
            TimeZoneInfo.ConvertTimeBySystemTimeZoneId(f.TakenAt!.Value, "UTC").Month == month &&
            TimeZoneInfo.ConvertTimeBySystemTimeZoneId(f.TakenAt!.Value, "UTC").Day == day);

    internal static IQueryable<Guid> BuildPersonTierQuery(AppDbContext db, int month, int day, double minConfidence) =>
        BuildBaseQuery(db, month, day)
            .Where(f => f.Tags.Any(t => t.Tag == "person" && t.Confidence >= minConfidence))
            .Select(f => f.Id);

    internal static IQueryable<Guid> BuildGeotaggedTierQuery(AppDbContext db, int month, int day) =>
        BuildBaseQuery(db, month, day)
            .Where(f => f.Metadata != null && f.Metadata.Latitude != null && f.Metadata.Longitude != null)
            .Select(f => f.Id);

    internal static IQueryable<Guid> BuildAnyTierQuery(AppDbContext db, int month, int day) =>
        BuildBaseQuery(db, month, day).Select(f => f.Id);
}
