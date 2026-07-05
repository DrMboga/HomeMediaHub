using Api.Data;
using Data.Entities;
using MediatR;
using Pgvector;

namespace Api.Features.Media.SaveMedia;

public sealed class SaveMediaHandler : IRequestHandler<SaveMediaRequest, SaveMediaResponse>
{
    private readonly AppDbContext _db;
    private readonly ILogger<SaveMediaHandler> _logger;

    public SaveMediaHandler(AppDbContext db, ILogger<SaveMediaHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<SaveMediaResponse> Handle(SaveMediaRequest req, CancellationToken ct)
    {
        _db.MediaFiles.Add(req.ToMediaFile());
        await _db.SaveChangesAsync(ct);

        // Embedding is saved separately so a failure here never rolls back or
        // blocks the file record — the file is always indexed, embeddings can
        // be backfilled later if this step fails.
        if (req.Embedding is { Length: 512 })
        {
            try
            {
                _db.MediaEmbeddings.Add(new MediaEmbedding
                {
                    MediaFileId = req.Id,
                    Embedding   = new Vector(req.Embedding),
                });
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding insert failed for media file {MediaFileId} — file saved without embedding", req.Id);
            }
        }

        return new SaveMediaResponse(req.Id);
    }
}
