using Api.Data;
using Api.Features.Media;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.GetReviewQueue;

public record GetReviewQueueRequest(int Limit = 100) : IRequest<IReadOnlyList<MediaListItemDto>>;

public sealed class GetReviewQueueHandler : IRequestHandler<GetReviewQueueRequest, IReadOnlyList<MediaListItemDto>>
{
    private readonly AppDbContext _db;

    public GetReviewQueueHandler(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<MediaListItemDto>> Handle(GetReviewQueueRequest req, CancellationToken ct)
    {
        var items = await _db.MediaFiles
            .Include(f => f.Thumbnail)
            .Include(f => f.Metadata)
            .Where(f => !f.IsDeleted &&
                        (f.OriginClass == "meme" || f.OriginClass == "screenshot" || f.OriginClass == "artwork"))
            .OrderByDescending(f => f.OriginConfidence)
            .Take(req.Limit)
            .ToListAsync(ct);

        return items.Select(f => f.ToListItemDto()).ToList();
    }
}
