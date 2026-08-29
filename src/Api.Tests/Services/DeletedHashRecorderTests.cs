using Api.Data;
using Api.Services;
using Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Tests.Services;

/// <summary>
/// Phase 14: the Review page's hard delete must remember what it removed by content, or the next
/// scan re-ingests any second copy still on disk. 167 photos came back that way on prod,
/// 2026-08-29.
/// </summary>
/// <remarks>
/// Covers <see cref="DeletedHashRecorder"/> rather than <c>BulkDeleteMediaHandler</c>: the handler
/// ends in <c>ExecuteDeleteAsync</c>, which the InMemory provider does not implement, so nothing
/// in it is reachable from this suite. Its file-deletion loop remains uncovered, as it was before.
/// </remarks>
public sealed class DeletedHashRecorderTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();

    public void Dispose() => _db.Dispose();

    private DeletedHashRecorder Recorder() => new(_db, NullLogger<DeletedHashRecorder>.Instance);

    private static MediaFile File(string hash) => new()
    {
        Id       = Guid.NewGuid(),
        FilePath = $"/photo/{Guid.NewGuid():N}.jpg",
        FileHash = hash,
        MimeType = "image/jpeg",
    };

    [Fact]
    public async Task Record_StoresTheHashOfEveryFile()
    {
        var recorded = await Recorder().RecordAsync([File("hash-a"), File("hash-b")], default);

        Assert.Equal(2, recorded);
        Assert.Equal(["hash-a", "hash-b"], (await _db.DeletedHashes.Select(d => d.FileHash).ToListAsync()).Order());
    }

    [Fact]
    public async Task Record_StoresTheLastKnownPath()
    {
        // Never read by the pipeline — it exists so a row can be explained months later without
        // joining to jobs that may themselves be gone.
        var a = File("hash-a");

        await Recorder().RecordAsync([a], default);

        Assert.Equal(a.FilePath, (await _db.DeletedHashes.SingleAsync()).LastFilePath);
    }

    [Fact]
    public async Task Record_HashAlreadyStored_IsANoOp()
    {
        // Deleted once, re-ingested from a copy before this table existed, deleted again. The hash
        // is the primary key, so a blind insert would raise 23505 mid-delete.
        _db.DeletedHashes.Add(new DeletedHash { FileHash = "hash-a", LastFilePath = "/photo/old.jpg" });
        await _db.SaveChangesAsync();

        var recorded = await Recorder().RecordAsync([File("hash-a")], default);

        Assert.Equal(0, recorded);
        var row = await _db.DeletedHashes.SingleAsync();
        Assert.Equal("/photo/old.jpg", row.LastFilePath);   // first deletion's path is kept
    }

    [Fact]
    public async Task Record_SameHashTwiceInOneBatch_StoresItOnce()
    {
        // media_files has a unique index on file_hash, so this cannot happen today — but the
        // recorder takes a plain list, and a duplicate in it must not fail the whole delete.
        var recorded = await Recorder().RecordAsync([File("hash-a"), File("hash-a")], default);

        Assert.Equal(1, recorded);
        Assert.Single(await _db.DeletedHashes.ToListAsync());
    }

    [Fact]
    public async Task Record_EmptyBatch_DoesNothing()
    {
        Assert.Equal(0, await Recorder().RecordAsync([], default));
        Assert.Empty(await _db.DeletedHashes.ToListAsync());
    }

    [Fact]
    public async Task Record_FileWithoutAHash_IsSkipped()
    {
        // Defensive: a row with an empty hash would otherwise become a deleted_hashes entry that
        // matches nothing and blocks nothing, but still shows up in the table forever.
        var recorded = await Recorder().RecordAsync([File(""), File("   "), File("hash-a")], default);

        Assert.Equal(1, recorded);
        Assert.Equal("hash-a", (await _db.DeletedHashes.SingleAsync()).FileHash);
    }
}
