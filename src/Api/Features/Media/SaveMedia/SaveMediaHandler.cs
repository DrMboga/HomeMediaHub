using Api.Data;
using MediatR;

namespace Api.Features.Media.SaveMedia;

public sealed class SaveMediaHandler : IRequestHandler<SaveMediaRequest, SaveMediaResponse>
{
    private readonly AppDbContext _db;
    public SaveMediaHandler(AppDbContext db) => _db = db;

    public async Task<SaveMediaResponse> Handle(SaveMediaRequest req, CancellationToken ct)
    {
        _db.MediaFiles.Add(req.ToMediaFile());
        await _db.SaveChangesAsync(ct);
        return new SaveMediaResponse(req.Id);
    }
}
