using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.DeleteMedia;

public record SoftDeleteMediaRequest(Guid Id) : IRequest<bool>;

public sealed class SoftDeleteMediaHandler : IRequestHandler<SoftDeleteMediaRequest, bool>
{
    private readonly AppDbContext _db;

    public SoftDeleteMediaHandler(AppDbContext db) => _db = db;

    public async Task<bool> Handle(SoftDeleteMediaRequest req, CancellationToken ct)
    {
        var affected = await _db.MediaFiles
            .Where(f => f.Id == req.Id && !f.IsDeleted)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsDeleted, true), ct);

        return affected > 0;
    }
}
