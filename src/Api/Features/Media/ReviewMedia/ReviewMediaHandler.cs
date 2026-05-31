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
        var file = await _db.MediaFiles
            .Where(f => f.Id == req.Id && !f.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (file is null) return false;

        if (req.Action == "meme")
            file.OriginClass = "meme";
        // "real" keeps the original AI classification intact
        file.OriginReviewed = true;

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
