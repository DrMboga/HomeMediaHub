using Api.Data;
using Api.Features.Admin;
using Data.Entities;

namespace Api.Tests.Features.Admin;

public sealed class GetPipelineStatusHandlerTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();

    public void Dispose() => _db.Dispose();

    private GetPipelineStatusHandler Handler() => new(_db);

    private static ProcessingJob Job(
        Guid scanId, string status,
        DateTime? startedAt = null, DateTime? completedAt = null) => new()
    {
        Id          = Guid.NewGuid(),
        FilePath    = $"/photos/{Guid.NewGuid()}.jpg",
        Source      = "manual",
        Status      = status,
        ScanId      = scanId,
        StartedAt   = startedAt ?? DateTime.UtcNow,
        CompletedAt = completedAt,
    };

    [Fact]
    public async Task Handle_NoJobs_ReturnsNullScanAndZeroCounts()
    {
        var result = await Handler().Handle(new GetPipelineStatusRequest(), default);

        Assert.Null(result.ScanId);
        Assert.Null(result.StartedAt);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.CompletedLastHour);
        Assert.Equal(0, result.StaleCount);
    }

    [Fact]
    public async Task Handle_CountsScopedToLatestScan_ByStatus()
    {
        var oldScan = Guid.NewGuid();
        _db.ProcessingJobs.Add(Job(oldScan, "completed", startedAt: DateTime.UtcNow.AddDays(-5)));

        var latestScan = Guid.NewGuid();
        _db.ProcessingJobs.AddRange(
            Job(latestScan, "queued"),
            Job(latestScan, "queued"),
            Job(latestScan, "processing"),
            Job(latestScan, "enriching"),
            Job(latestScan, "completed"),
            Job(latestScan, "failed"),
            Job(latestScan, "skipped_duplicate"));
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(new GetPipelineStatusRequest(), default);

        Assert.Equal(latestScan, result.ScanId);
        Assert.Equal(7, result.Total);
        Assert.Equal(2, result.Counts.Queued);
        Assert.Equal(1, result.Counts.Processing);
        Assert.Equal(1, result.Counts.Enriching);
        Assert.Equal(1, result.Counts.Completed);
        Assert.Equal(1, result.Counts.Failed);
        Assert.Equal(1, result.Counts.SkippedDuplicate);
    }

    [Fact]
    public async Task Handle_CompletedLastHour_CountsAcrossAllScans()
    {
        var scan = Guid.NewGuid();
        _db.ProcessingJobs.AddRange(
            Job(scan, "completed", completedAt: DateTime.UtcNow.AddMinutes(-10)),
            Job(scan, "completed", completedAt: DateTime.UtcNow.AddDays(-2)));
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(new GetPipelineStatusRequest(), default);

        Assert.Equal(1, result.CompletedLastHour);
    }

    [Fact]
    public async Task Handle_EnrichingOverA_Day_CountsAsStale()
    {
        var scan = Guid.NewGuid();
        _db.ProcessingJobs.AddRange(
            Job(scan, "enriching", startedAt: DateTime.UtcNow.AddHours(-30)),
            Job(scan, "enriching", startedAt: DateTime.UtcNow.AddMinutes(-5)));
        await _db.SaveChangesAsync();

        var result = await Handler().Handle(new GetPipelineStatusRequest(), default);

        Assert.Equal(1, result.StaleCount);
    }
}
