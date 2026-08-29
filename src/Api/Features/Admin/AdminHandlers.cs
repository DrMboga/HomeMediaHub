using Api.Data;
using Contracts.Messages;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Api.Features.Admin;

// ── DTOs ────────────────────────────────────────────────────────────────────

public record JobDto(
    Guid Id, string FilePath, string Source, string Status,
    Guid? ScanId, Guid? MediaFileId, DateTime StartedAt, DateTime? CompletedAt,
    string? FailedStep, string? ErrorMessage, int RetryCount);

public record JobStatusCounts(
    int Queued, int Processing, int Enriching, int Completed, int Failed, int SkippedDuplicate,
    int SkippedDeleted);

public record PipelineStatusDto(
    Guid? ScanId, DateTime? StartedAt, JobStatusCounts Counts, int Total,
    int CompletedLastHour, int StaleCount);

// ── Requests ────────────────────────────────────────────────────────────────

public record TriggerScanRequest(string? Path) : IRequest<Guid>;

public record GetPipelineStatusRequest : IRequest<PipelineStatusDto>;

public record GetJobsRequest(string? Status, int Page = 1, int PageSize = 50)
    : IRequest<IReadOnlyList<JobDto>>;

public record GetJobDetailRequest(Guid Id) : IRequest<JobDto?>;

public record RetryJobRequest(Guid Id) : IRequest<bool>;

public record RetryFailedJobsRequest : IRequest<int>;

// ── Handlers ────────────────────────────────────────────────────────────────

public sealed class TriggerScanHandler : IRequestHandler<TriggerScanRequest, Guid>
{
    private readonly IPublishEndpoint _publish;
    private readonly IConfiguration _config;

    public TriggerScanHandler(IPublishEndpoint publish, IConfiguration config)
    {
        _publish = publish;
        _config  = config;
    }

    public async Task<Guid> Handle(TriggerScanRequest req, CancellationToken ct)
    {
        var scanId = Guid.NewGuid();
        var path   = string.IsNullOrWhiteSpace(req.Path)
            ? _config["Scan:DefaultPath"] ?? "/photo"
            : req.Path;

        await _publish.Publish(new ScanRequested(
            ScanId:      scanId,
            Path:        path,
            Source:      "manual",
            TriggeredBy: "admin",
            RequestedAt: DateTimeOffset.UtcNow), ct);

        return scanId;
    }
}

public sealed class GetPipelineStatusHandler : IRequestHandler<GetPipelineStatusRequest, PipelineStatusDto>
{
    private readonly AppDbContext _db;
    public GetPipelineStatusHandler(AppDbContext db) => _db = db;

    public async Task<PipelineStatusDto> Handle(GetPipelineStatusRequest req, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var completedLastHour = await _db.ProcessingJobs
            .CountAsync(j => j.Status == "completed" && j.CompletedAt >= now.AddHours(-1), ct);

        var staleCount = await _db.ProcessingJobs
            .CountAsync(j => j.Status == "enriching" && j.StartedAt <= now.AddHours(-24), ct);

        var latestScanId = await _db.ProcessingJobs
            .Where(j => j.ScanId != null)
            .OrderByDescending(j => j.StartedAt)
            .Select(j => j.ScanId)
            .FirstOrDefaultAsync(ct);

        if (latestScanId is null)
        {
            return new PipelineStatusDto(
                null, null, new JobStatusCounts(0, 0, 0, 0, 0, 0, 0), 0, completedLastHour, staleCount);
        }

        var scanJobs = _db.ProcessingJobs.Where(j => j.ScanId == latestScanId);

        var statusCounts = await scanJobs
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

        var startedAt = await scanJobs.MinAsync(j => (DateTime?)j.StartedAt, ct);
        var total     = statusCounts.Values.Sum();

        var counts = new JobStatusCounts(
            Queued:           statusCounts.GetValueOrDefault("queued"),
            Processing:       statusCounts.GetValueOrDefault("processing"),
            Enriching:        statusCounts.GetValueOrDefault("enriching"),
            Completed:        statusCounts.GetValueOrDefault("completed"),
            Failed:           statusCounts.GetValueOrDefault("failed"),
            SkippedDuplicate: statusCounts.GetValueOrDefault("skipped_duplicate"),
            SkippedDeleted:   statusCounts.GetValueOrDefault("skipped_deleted"));

        return new PipelineStatusDto(latestScanId, startedAt, counts, total, completedLastHour, staleCount);
    }
}

public sealed class GetJobsHandler : IRequestHandler<GetJobsRequest, IReadOnlyList<JobDto>>
{
    private readonly AppDbContext _db;
    public GetJobsHandler(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<JobDto>> Handle(GetJobsRequest req, CancellationToken ct)
    {
        var q = _db.ProcessingJobs.AsQueryable();
        if (!string.IsNullOrEmpty(req.Status))
            q = q.Where(j => j.Status == req.Status);

        return await q
            .OrderByDescending(j => j.StartedAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(j => new JobDto(j.Id, j.FilePath, j.Source, j.Status,
                j.ScanId, j.MediaFileId, j.StartedAt, j.CompletedAt,
                j.FailedStep, j.ErrorMessage, j.RetryCount))
            .ToListAsync(ct);
    }
}

public sealed class GetJobDetailHandler : IRequestHandler<GetJobDetailRequest, JobDto?>
{
    private readonly AppDbContext _db;
    public GetJobDetailHandler(AppDbContext db) => _db = db;

    public async Task<JobDto?> Handle(GetJobDetailRequest req, CancellationToken ct)
    {
        var j = await _db.ProcessingJobs.Where(x => x.Id == req.Id).FirstOrDefaultAsync(ct);
        if (j is null) return null;

        return new JobDto(j.Id, j.FilePath, j.Source, j.Status,
            j.ScanId, j.MediaFileId, j.StartedAt, j.CompletedAt,
            j.FailedStep, j.ErrorMessage, j.RetryCount);
    }
}

public sealed class RetryJobHandler : IRequestHandler<RetryJobRequest, bool>
{
    private readonly AppDbContext _db;
    private readonly IPublishEndpoint _publish;

    public RetryJobHandler(AppDbContext db, IPublishEndpoint publish)
    {
        _db      = db;
        _publish = publish;
    }

    public async Task<bool> Handle(RetryJobRequest req, CancellationToken ct)
    {
        var job = await _db.ProcessingJobs
            .AsTracking()
            .Where(j => j.Id == req.Id && j.Status == "failed")
            .FirstOrDefaultAsync(ct);

        if (job is null) return false;

        job.Status       = "queued";
        job.RetryCount  += 1;
        job.CompletedAt  = null;
        job.FailedStep   = null;
        job.ErrorMessage = null;
        await _db.SaveChangesAsync(ct);

        await _publish.Publish(new FileDiscovered(
            JobId:        job.Id,
            ScanId:       job.ScanId ?? Guid.NewGuid(),
            FilePath:     job.FilePath,
            Source:       job.Source,
            Reprocess:    true,
            DiscoveredAt: DateTimeOffset.UtcNow), ct);

        return true;
    }
}

public sealed class RetryFailedJobsHandler : IRequestHandler<RetryFailedJobsRequest, int>
{
    private readonly AppDbContext _db;
    private readonly IPublishEndpoint _publish;

    public RetryFailedJobsHandler(AppDbContext db, IPublishEndpoint publish)
    {
        _db      = db;
        _publish = publish;
    }

    public async Task<int> Handle(RetryFailedJobsRequest req, CancellationToken ct)
    {
        var failed = await _db.ProcessingJobs
            .AsTracking()
            .Where(j => j.Status == "failed")
            .ToListAsync(ct);
        if (failed.Count == 0) return 0;

        foreach (var job in failed)
        {
            job.Status       = "queued";
            job.RetryCount  += 1;
            job.CompletedAt  = null;
            job.FailedStep   = null;
            job.ErrorMessage = null;
        }
        await _db.SaveChangesAsync(ct);

        foreach (var job in failed)
        {
            await _publish.Publish(new FileDiscovered(
                JobId:        job.Id,
                ScanId:       job.ScanId ?? Guid.NewGuid(),
                FilePath:     job.FilePath,
                Source:       job.Source,
                Reprocess:    true,
                DiscoveredAt: DateTimeOffset.UtcNow), ct);
        }

        return failed.Count;
    }
}
