using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.ReviewMedia;

public record ReviewMediaRequest(Guid Id, string Action) : IRequest<bool>;

public sealed class ReviewMediaHandler : IRequestHandler<ReviewMediaRequest, bool>
{
    private readonly AppDbContext _db;

    public ReviewMediaHandler(AppDbContext db) => _db = db;

    public async Task<bool> Handle(ReviewMediaRequest req, CancellationToken ct)
    {
        var base_q = _db.MediaFiles.Where(f => f.Id == req.Id && !f.IsDeleted);

        int updated = req.Action == "meme"
            ? await base_q.ExecuteUpdateAsync(s => s
                .SetProperty(f => f.OriginClass, "meme")
                .SetProperty(f => f.OriginReviewed, true), ct)
            : await base_q.ExecuteUpdateAsync(s => s
                .SetProperty(f => f.OriginReviewed, true), ct);

        return updated > 0;
    }
}
