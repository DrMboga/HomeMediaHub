using Api.Data;
using Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Remembers, by content hash, what the user deleted from the library, so ingest can refuse a
/// second copy of the same photo from another path.
/// </summary>
/// <remarks>
/// Its own class rather than a private method on <c>BulkDeleteMediaHandler</c> for one reason:
/// that handler ends in <c>ExecuteDeleteAsync</c>, which the InMemory provider the test suite runs
/// on does not implement, so nothing inside it can be covered. This can be.
///
/// The problem it solves, measured on prod 2026-08-29: dedup matches a hash against
/// <c>media_files</c>, so deleting the indexed copy of a photo un-shadows every duplicate of it
/// still on disk, and the next scan ingests one as new. 167 photos came back that way.
/// </remarks>
public sealed class DeletedHashRecorder
{
    private readonly AppDbContext _db;
    private readonly ILogger<DeletedHashRecorder> _log;

    public DeletedHashRecorder(AppDbContext db, ILogger<DeletedHashRecorder> log)
    {
        _db  = db;
        _log = log;
    }

    /// <returns>How many hashes were newly recorded.</returns>
    public async Task<int> RecordAsync(IReadOnlyCollection<MediaFile> files, CancellationToken ct)
    {
        var withHash = files.Where(f => !string.IsNullOrWhiteSpace(f.FileHash)).ToList();
        if (withHash.Count == 0) return 0;

        // A hash can already be here: deleted once, re-ingested from a copy, deleted again. The
        // hash is the primary key, so read first rather than letting SaveChanges raise a 23505 —
        // the set is one Review page at most, and the read is a single index probe.
        var hashes = withHash.Select(f => f.FileHash).ToList();
        var known = await _db.DeletedHashes
            .Where(d => hashes.Contains(d.FileHash))
            .Select(d => d.FileHash)
            .ToHashSetAsync(ct);

        var added = 0;
        foreach (var f in withHash)
        {
            if (!known.Add(f.FileHash)) continue;   // also guards duplicates within this batch
            _db.DeletedHashes.Add(new DeletedHash
            {
                FileHash     = f.FileHash,
                LastFilePath = f.FilePath,
                DeletedAt    = DateTime.UtcNow,
            });
            added++;
        }

        if (added == 0) return 0;

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Recorded {Count} deleted hash(es); they will not be re-ingested", added);
        return added;
    }
}
