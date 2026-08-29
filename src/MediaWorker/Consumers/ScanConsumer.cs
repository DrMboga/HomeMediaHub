using Contracts.Messages;
using Data.Entities;
using MassTransit;
using MediaWorker.Sagas;
using MediaWorker.Services;

namespace MediaWorker.Consumers;

/// <summary>
/// Consumes <see cref="ScanRequested"/>: enumerates the library, inserts a <c>queued</c>
/// processing_jobs row per new file (batched ~1000 per SaveChanges to survive a 190K-file burst),
/// and publishes <see cref="FileDiscovered"/> for each. One scan at a time (prefetch 1).
/// </summary>
public sealed class ScanConsumer : IConsumer<ScanRequested>
{
    private const int BatchSize = 1000;

    private readonly WorkerDbContext _db;
    private readonly ScanService _scan;
    private readonly ILogger<ScanConsumer> _log;

    public ScanConsumer(WorkerDbContext db, ScanService scan, ILogger<ScanConsumer> log)
    {
        _db = db;
        _scan = scan;
        _log = log;
    }

    public async Task Consume(ConsumeContext<ScanRequested> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (!Directory.Exists(msg.Path))
        {
            _log.LogWarning("Scan {ScanId}: path {Path} does not exist — nothing to do", msg.ScanId, msg.Path);
            return;
        }

        var discovery = await _scan.DiscoverAsync(msg.Path, ct);
        var files = discovery.Files;
        _log.LogInformation("Scan {ScanId}: discovered {Count} new files under {Path}", msg.ScanId, files.Count, msg.Path);

        // Logged even at zero: "the scan skipped nothing" is an answer, and a silent filter is one
        // you cannot tell apart from a scan that never saw the file.
        _log.LogInformation(
            "Scan {ScanId}: skipped {Sidecars} AppleDouble sidecar(s), {Empty} zero-length file(s) " +
            "and {TooRecent} file(s) written too recently; pruned {Pruned} excluded director(ies)",
            msg.ScanId, discovery.SkippedSidecars, discovery.SkippedEmpty,
            discovery.SkippedTooRecent, discovery.PrunedDirectories);

        var batch = new List<ProcessingJob>(BatchSize);
        foreach (var file in files)
        {
            var job = new ProcessingJob
            {
                Id       = Guid.NewGuid(),
                FilePath = file,
                Source   = msg.Source,
                Status   = "queued",
                ScanId   = msg.ScanId,
            };
            batch.Add(job);
            _db.ProcessingJobs.Add(job);

            if (batch.Count >= BatchSize)
                await FlushAsync(context, batch, ct);
        }
        await FlushAsync(context, batch, ct);
    }

    // Persist the batch of queued jobs, then publish FileDiscovered — the job row must exist
    // before the IngestConsumer can transition it.
    private async Task FlushAsync(ConsumeContext context, List<ProcessingJob> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        await _db.SaveChangesAsync(ct);
        foreach (var job in batch)
        {
            await context.Publish(new FileDiscovered(
                JobId:        job.Id,
                ScanId:       job.ScanId!.Value,
                FilePath:     job.FilePath,
                Source:       job.Source,
                Reprocess:    false,
                DiscoveredAt: DateTimeOffset.UtcNow));
        }
        batch.Clear();
    }
}
