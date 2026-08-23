using Api.Data;
using Api.Features.Media;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.GetReviewQueue;

public record GetReviewQueueRequest(int Page = 1, int PageSize = 100) : IRequest<PagedReviewQueueResult>;
public record PagedReviewQueueResult(IReadOnlyList<MediaListItemDto> Items, int Total);

public sealed class GetReviewQueueHandler : IRequestHandler<GetReviewQueueRequest, PagedReviewQueueResult>
{
    private readonly AppDbContext _db;

    public GetReviewQueueHandler(AppDbContext db) => _db = db;

    public async Task<PagedReviewQueueResult> Handle(GetReviewQueueRequest req, CancellationToken ct)
    {
        var q = _db.MediaFiles
            .Where(f => !f.IsDeleted &&
                        !f.OriginReviewed &&
                        (f.OriginClass == "screenshot" || f.OriginClass == "document" ||
                         f.OriginClass == "artwork"    || f.OriginClass == "reference") &&
                        f.Metadata.Latitude == null);

        var total = await q.CountAsync(ct);

        var items = await q
            .Include(f => f.Thumbnail)
            .Include(f => f.Metadata)
            // .OrderByDescending(f => f.OriginConfidence)
            .OrderByDescending(f => f.TakenAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .ToListAsync(ct);

        return new PagedReviewQueueResult(items.Select(f => f.ToListItemDto()).ToList(), total);
    }
}
