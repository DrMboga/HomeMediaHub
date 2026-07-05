using Data.Entities;
using MediaWorker.Sagas;
using MediaWorker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaWorker.Tests.Services;

public sealed class JobStoreTests
{
    private static async Task<(WorkerDbContext Db, IJobStore Store, Guid JobId)> SeedAsync(string status)
    {
        var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.ProcessingJobs.Add(new ProcessingJob { Id = jobId, FilePath = "/photo/x.jpg", Status = status });
        await db.SaveChangesAsync();
        return (db, new JobStore(db, NullLogger<JobStore>.Instance), jobId);
    }

    private static Task<ProcessingJob> ReloadAsync(WorkerDbContext db, Guid jobId) =>
        db.ProcessingJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);

    [Fact]
    public async Task MarkProcessing_SetsStatus()
    {
        var (db, store, jobId) = await SeedAsync("queued");
        await store.MarkProcessingAsync(jobId, CancellationToken.None);

        var job = await ReloadAsync(db, jobId);
        Assert.Equal("processing", job.Status);
        Assert.Null(job.CompletedAt);
    }

    [Fact]
    public async Task MarkEnriching_SetsStatusAndMediaFileId()
    {
        var (db, store, jobId) = await SeedAsync("processing");
        var mediaId = Guid.NewGuid();
        await store.MarkEnrichingAsync(jobId, mediaId, CancellationToken.None);

        var job = await ReloadAsync(db, jobId);
        Assert.Equal("enriching", job.Status);
        Assert.Equal(mediaId, job.MediaFileId);
    }

    [Fact]
    public async Task MarkSkippedDuplicate_SetsStatusMediaIdAndCompletedAt()
    {
        var (db, store, jobId) = await SeedAsync("processing");
        var dupId = Guid.NewGuid();
        await store.MarkSkippedDuplicateAsync(jobId, dupId, CancellationToken.None);

        var job = await ReloadAsync(db, jobId);
        Assert.Equal("skipped_duplicate", job.Status);
        Assert.Equal(dupId, job.MediaFileId);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task MarkCompleted_SetsStatusAndCompletedAt()
    {
        var (db, store, jobId) = await SeedAsync("enriching");
        await store.MarkCompletedAsync(jobId, CancellationToken.None);

        var job = await ReloadAsync(db, jobId);
        Assert.Equal("completed", job.Status);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task MarkFailed_SetsStatusStepErrorAndCompletedAt()
    {
        var (db, store, jobId) = await SeedAsync("enriching");
        await store.MarkFailedAsync(jobId, "ai", "unreadable image", CancellationToken.None);

        var job = await ReloadAsync(db, jobId);
        Assert.Equal("failed", job.Status);
        Assert.Equal("ai", job.FailedStep);
        Assert.Equal("unreadable image", job.ErrorMessage);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task UnknownJob_IsNoOp()
    {
        var (_, store, _) = await SeedAsync("queued");
        // Should not throw for a job id that does not exist.
        await store.MarkCompletedAsync(Guid.NewGuid(), CancellationToken.None);
    }
}
