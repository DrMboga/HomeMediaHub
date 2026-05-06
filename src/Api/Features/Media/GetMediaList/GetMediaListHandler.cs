using Api.Data;
using Api.Features.Media;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.GetMediaList;

public record GetMediaListRequest(
    int Page,
    int PageSize,
    DateTime? From,
    DateTime? To,
    string? Tag,
    bool? Favorite,
    bool? IsVideo = null,
    bool? NoDate = null
) : IRequest<PagedResult<MediaListItemDto>>;

public sealed class GetMediaListHandler : IRequestHandler<GetMediaListRequest, PagedResult<MediaListItemDto>>
{
    private readonly AppDbContext _db;

    public GetMediaListHandler(AppDbContext db) => _db = db;

    public async Task<PagedResult<MediaListItemDto>> Handle(GetMediaListRequest req, CancellationToken ct)
    {
        var q = _db.MediaFiles
            .Include(f => f.Thumbnail)
            .Include(f => f.Metadata)
            .Where(f => !f.IsDeleted)
            .AsQueryable();

        if (req.NoDate == true)    q = q.Where(f => f.TakenAt == null);
        else
        {
            if (req.From.HasValue) q = q.Where(f => f.TakenAt >= DateTime.SpecifyKind(req.From.Value, DateTimeKind.Utc));
            if (req.To.HasValue)   q = q.Where(f => f.TakenAt <= DateTime.SpecifyKind(req.To.Value, DateTimeKind.Utc));
        }
        if (req.Favorite.HasValue) q = q.Where(f => f.IsFavorite == req.Favorite.Value);
        if (req.IsVideo.HasValue)  q = req.IsVideo.Value
            ? q.Where(f => f.DurationSec.HasValue)
            : q.Where(f => !f.DurationSec.HasValue);
        if (!string.IsNullOrEmpty(req.Tag))
            q = q.Where(f => f.Tags.Any(t => t.Tag == req.Tag));

        var total = await q.CountAsync(ct);
        var items = await q
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
