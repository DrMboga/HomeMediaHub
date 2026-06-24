using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Tools.Dedup;

public sealed class DedupHandler : IRequestHandler<DedupRequest, DedupResponse>
{
    private readonly AppDbContext _db;
    public DedupHandler(AppDbContext db) => _db = db;

    public async Task<DedupResponse> Handle(DedupRequest request, CancellationToken cancellationToken)
    {
        var id = await _db.MediaFiles
            .Where(f => f.FileHash == request.Hash)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return new DedupResponse(id.HasValue, id);
    }
}
