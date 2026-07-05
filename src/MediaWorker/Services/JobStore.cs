using Data.Entities;
using MediaWorker.Sagas;
using Microsoft.EntityFrameworkCore;

namespace MediaWorker.Services;

/// <summary>
/// The single owner of <c>processing_jobs</c> lifecycle transitions — so consumers orchestrate the
/// pipeline without juggling tracked entities or scattering <c>SaveChanges</c> across helper
/// methods. This is a focused, intention-revealing store (not a generic repository): reads/paging
/// live in the Api, and the initial bulk insert of <c>queued</c> rows stays in
/// <see cref="Consumers.ScanConsumer"/> (a cohesive single operation).
/// </summary>
/// <remarks>
/// Uses tracked load + <c>SaveChanges</c> rather than <c>ExecuteUpdateAsync</c> so the EF in-memory
/// test provider (used by the consumer harness tests) works. The single-row primary-key load per
/// transition is negligible against ingest I/O; within one message scope the same context is shared,
/// so repeated transitions resolve to the already-tracked instance.
/// </remarks>
public interface IJobStore
{
    Task MarkProcessingAsync(Guid jobId, CancellationToken ct);
    Task MarkEnrichingAsync(Guid jobId, Guid mediaFileId, CancellationToken ct);
    Task MarkSkippedDuplicateAsync(Guid jobId, Guid duplicateMediaFileId, CancellationToken ct);
    Task MarkCompletedAsync(Guid jobId, CancellationToken ct);
    Task MarkFailedAsync(Guid jobId, string failedStep, string? error, CancellationToken ct);
}

public sealed class JobStore : IJobStore
{
    private readonly WorkerDbContext _db;
    private readonly ILogger<JobStore> _log;

    public JobStore(WorkerDbContext db, ILogger<JobStore> log)
    {
        _db = db;
        _log = log;
    }

    public Task MarkProcessingAsync(Guid jobId, CancellationToken ct) =>
        UpdateAsync(jobId, job => job.Status = "processing", ct);

    public Task MarkEnrichingAsync(Guid jobId, Guid mediaFileId, CancellationToken ct) =>
        UpdateAsync(jobId, job =>
        {
            job.Status      = "enriching";
            job.MediaFileId = mediaFileId;
        }, ct);

    public Task MarkSkippedDuplicateAsync(Guid jobId, Guid duplicateMediaFileId, CancellationToken ct) =>
        UpdateAsync(jobId, job =>
        {
            job.Status      = "skipped_duplicate";
            job.MediaFileId = duplicateMediaFileId;
            job.CompletedAt = DateTime.UtcNow;
        }, ct);

    public Task MarkCompletedAsync(Guid jobId, CancellationToken ct) =>
        UpdateAsync(jobId, job =>
        {
            job.Status      = "completed";
            job.CompletedAt = DateTime.UtcNow;
        }, ct);

    public Task MarkFailedAsync(Guid jobId, string failedStep, string? error, CancellationToken ct) =>
        UpdateAsync(jobId, job =>
        {
            job.Status       = "failed";
            job.FailedStep   = failedStep;
            job.ErrorMessage = error;
            job.CompletedAt  = DateTime.UtcNow;
        }, ct);

    private async Task UpdateAsync(Guid jobId, Action<ProcessingJob> mutate, CancellationToken ct)
    {
        var job = await _db.ProcessingJobs.AsTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            _log.LogWarning("JobStore: job {JobId} not found — transition skipped", jobId);
            return;
        }
        mutate(job);
        await _db.SaveChangesAsync(ct);
    }
}
