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

    /// <summary>
    /// True if this content was deliberately deleted from the library. Checked after
    /// <see cref="FindByHashAsync"/>, because a live photo with the same hash is the stronger
    /// answer: it means the content is present, whatever was deleted before.
    /// </summary>
    public Task<bool> WasDeletedAsync(string hash, CancellationToken ct) =>
        _db.DeletedHashes.AnyAsync(d => d.FileHash == hash, ct);
}
