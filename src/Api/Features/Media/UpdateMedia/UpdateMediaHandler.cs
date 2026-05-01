using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.UpdateMedia;

public record UpdateMediaRequest(Guid Id, bool? IsFavorite, Guid? SeriesId) : IRequest<bool>;

public sealed class UpdateMediaHandler : IRequestHandler<UpdateMediaRequest, bool>
{
    private readonly AppDbContext _db;

    public UpdateMediaHandler(AppDbContext db) => _db = db;

    public async Task<bool> Handle(UpdateMediaRequest req, CancellationToken ct)
    {
        var f = await _db.MediaFiles
            .AsTracking()
            .Where(x => x.Id == req.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (f is null) return false;

        if (req.IsFavorite.HasValue) f.IsFavorite = req.IsFavorite.Value;

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
