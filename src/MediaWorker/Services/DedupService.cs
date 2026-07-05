using MediaWorker.Sagas;
using Microsoft.EntityFrameworkCore;

namespace MediaWorker.Services;

/// <summary>
/// Looks up an existing media file by content hash (ported from the old <c>DedupHandler</c>).
/// The unique index on <c>media_files(file_hash)</c> is the final arbiter; this is the cheap
/// pre-check so we can skip conversion/thumbnailing for an obvious duplicate.
/// </summary>
public sealed class DedupService
{
    private readonly WorkerDbContext _db;
    public DedupService(WorkerDbContext db) => _db = db;

    public async Task<Guid?> FindByHashAsync(string hash, CancellationToken ct) =>
        await _db.MediaFiles
            .Where(f => f.FileHash == hash)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(ct);
}
