using Contracts.Messages;
using Data.Entities;
using MassTransit;
using MediaWorker.Sagas;
using MediaWorker.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace MediaWorker.Consumers;

/// <summary>
/// Applies the enrichment results to the already-saved media row (architecture §2, final step):
/// origin class, embedding upsert, tag replacement, and reverse-geocoded address. Pure idempotent
/// update — consuming the same message twice is harmless. The <c>/shared-temp</c> work file is
/// deleted only when AI succeeded (kept for retry otherwise). Marks the job
/// <c>completed</c> or <c>failed</c> via <see cref="IJobStore"/> (media row stays visible either way).
/// </summary>
public sealed class ApplyEnrichmentConsumer : IConsumer<ApplyEnrichmentRequested>
{
    private readonly WorkerDbContext _db;
    private readonly IJobStore _jobs;
    private readonly ILogger<ApplyEnrichmentConsumer> _log;

    public ApplyEnrichmentConsumer(WorkerDbContext db, IJobStore jobs, ILogger<ApplyEnrichmentConsumer> log)
    {
        _db = db;
        _jobs = jobs;
        _log = log;
    }

    public async Task Consume(ConsumeContext<ApplyEnrichmentRequested> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;

        var file = await _db.MediaFiles.AsTracking()
            .FirstOrDefaultAsync(f => f.Id == m.MediaFileId, ct);
        if (file is null)
        {
            _log.LogWarning("ApplyEnrichment: media file {MediaFileId} not found — skipping", m.MediaFileId);
            return;
        }

        if (m.AiSuccess)
        {
            file.OriginClass      = m.OriginClass;
            file.OriginConfidence = m.OriginConfidence;

            await ReplaceTagsAsync(m.MediaFileId, m.Tags, ct);
            if (m.Embedding is { Length: 512 })
                await UpsertEmbeddingAsync(m.MediaFileId, m.Embedding, ct);
        }

        if (m.GeoSuccess && m.Address is not null)
            await UpsertAddressAsync(file, m.Address, ct);

        await _db.SaveChangesAsync(ct);

        // Work file kept for retry when AI failed; removed once AI succeeded.
        if (m.AiSuccess) TryDelete(m.WorkFilePath);

        await UpdateJobStatusAsync(m, ct);
    }

    private async Task ReplaceTagsAsync(Guid mediaFileId, AiTag[] tags, CancellationToken ct)
    {
        var existing = await _db.MediaTags.AsTracking()
            .Where(t => t.MediaFileId == mediaFileId).ToListAsync(ct);
        _db.MediaTags.RemoveRange(existing);

        if (tags is { Length: > 0 })
        {
            _db.MediaTags.AddRange(tags.Select(t => new MediaTag
            {
                Id          = Guid.NewGuid(),
                MediaFileId = mediaFileId,
                Tag         = t.Tag,
                Confidence  = (float)t.Confidence,
                Model       = t.Model,
            }));
        }
    }

    private async Task UpsertEmbeddingAsync(Guid mediaFileId, float[] embedding, CancellationToken ct)
    {
        var existing = await _db.MediaEmbeddings.AsTracking()
            .FirstOrDefaultAsync(e => e.MediaFileId == mediaFileId, ct);
        if (existing is null)
        {
            _db.MediaEmbeddings.Add(new MediaEmbedding
            {
                MediaFileId = mediaFileId,
                Embedding   = new Vector(embedding),
            });
        }
        else
        {
            existing.Embedding = new Vector(embedding);
        }
    }

    private async Task UpsertAddressAsync(MediaFile file, string address, CancellationToken ct)
    {
        var meta = await _db.MediaMetadata.AsTracking()
            .FirstOrDefaultAsync(x => x.MediaFileId == file.Id, ct);
        if (meta is null)
            _db.MediaMetadata.Add(new MediaMetadata { MediaFileId = file.Id, Address = address });
        else
            meta.Address = address;
    }

    private async Task UpdateJobStatusAsync(ApplyEnrichmentRequested m, CancellationToken ct)
    {
        var aiFailed  = !m.AiSuccess;
        var geoFailed = !m.GeoSuccess && m.GeoError is not null;

        if (aiFailed || geoFailed)
        {
            var step  = aiFailed ? "ai" : "geo";
            var error = (aiFailed, geoFailed) switch
            {
                (true, true) => $"ai: {m.AiError}; geo: {m.GeoError}",
                (true, _)    => m.AiError,
                _            => m.GeoError,
            };
            await _jobs.MarkFailedAsync(m.JobId, step, error, ct);
        }
        else
        {
            await _jobs.MarkCompletedAsync(m.JobId, ct);
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort temp cleanup */ }
    }
}
