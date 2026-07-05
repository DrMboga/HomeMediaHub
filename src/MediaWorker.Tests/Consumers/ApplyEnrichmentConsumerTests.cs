using Contracts.Messages;
using Data.Entities;
using MassTransit;
using MediaWorker.Consumers;
using MediaWorker.Sagas;
using MediaWorker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MediaWorker.Tests.Consumers;

public sealed class ApplyEnrichmentConsumerTests
{
    private static ConsumeContext<ApplyEnrichmentRequested> Ctx(ApplyEnrichmentRequested msg)
    {
        var ctx = Substitute.For<ConsumeContext<ApplyEnrichmentRequested>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static async Task ConsumeAsync(string dbName, ApplyEnrichmentRequested msg)
    {
        // Fresh context per consume — mimics a new consume scope. JobStore shares that context.
        using var db = TestDbContextFactory.CreateShared(dbName);
        var jobs = new JobStore(db, NullLogger<JobStore>.Instance);
        await new ApplyEnrichmentConsumer(db, jobs, NullLogger<ApplyEnrichmentConsumer>.Instance).Consume(Ctx(msg));
    }

    [Fact]
    public async Task DoubleConsume_IsIdempotent()
    {
        var dbName = Guid.NewGuid().ToString();
        var mediaId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        using (var seed = TestDbContextFactory.CreateShared(dbName))
        {
            seed.MediaFiles.Add(new MediaFile { Id = mediaId, FilePath = "/photo/x.jpg", FileHash = "h" });
            seed.ProcessingJobs.Add(new ProcessingJob { Id = jobId, FilePath = "/photo/x.jpg", Status = "enriching" });
            await seed.SaveChangesAsync();
        }

        var msg = new ApplyEnrichmentRequested(
            MediaFileId: mediaId, JobId: jobId, WorkFilePath: null,
            AiSuccess: true, Tags: [new AiTag("dog", 0.9, "yolov8n")], Embedding: [],
            OriginClass: "photo", OriginConfidence: 0.93, AiError: null,
            GeoSuccess: false, Address: null, GeoError: null);

        await ConsumeAsync(dbName, msg);
        await ConsumeAsync(dbName, msg); // second delivery must not duplicate

        using var check = TestDbContextFactory.CreateShared(dbName);
        var file = await check.MediaFiles.FirstAsync(f => f.Id == mediaId);
        Assert.Equal("photo", file.OriginClass);
        Assert.Equal(1, await check.MediaTags.CountAsync(t => t.MediaFileId == mediaId)); // replaced, not appended

        var job = await check.ProcessingJobs.FirstAsync(j => j.Id == jobId);
        Assert.Equal("completed", job.Status);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task AiFailure_KeepsWorkFile_AndMarksJobFailed()
    {
        var dbName = Guid.NewGuid().ToString();
        var mediaId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        // A real temp file stands in for the /shared-temp work file.
        var workFile = Path.Combine(Path.GetTempPath(), $"hmh-work-{Guid.NewGuid()}.jpg");
        await File.WriteAllTextAsync(workFile, "frame");

        try
        {
            using (var seed = TestDbContextFactory.CreateShared(dbName))
            {
                seed.MediaFiles.Add(new MediaFile { Id = mediaId, FilePath = "/photo/y.heic", FileHash = "h" });
                seed.ProcessingJobs.Add(new ProcessingJob { Id = jobId, FilePath = "/photo/y.heic", Status = "enriching" });
                await seed.SaveChangesAsync();
            }

            var msg = new ApplyEnrichmentRequested(
                MediaFileId: mediaId, JobId: jobId, WorkFilePath: workFile,
                AiSuccess: false, Tags: [], Embedding: [],
                OriginClass: null, OriginConfidence: null, AiError: "unreadable image",
                GeoSuccess: false, Address: null, GeoError: null);

            await ConsumeAsync(dbName, msg);

            Assert.True(File.Exists(workFile)); // kept for retry because AI failed

            using var check = TestDbContextFactory.CreateShared(dbName);
            var job = await check.ProcessingJobs.FirstAsync(j => j.Id == jobId);
            Assert.Equal("failed", job.Status);
            Assert.Equal("ai", job.FailedStep);
        }
        finally
        {
            if (File.Exists(workFile)) File.Delete(workFile);
        }
    }
}
