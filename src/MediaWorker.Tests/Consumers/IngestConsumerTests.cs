using Contracts.Messages;
using Data.Entities;
using MassTransit;
using MassTransit.Testing;
using MediaWorker.Consumers;
using MediaWorker.Sagas;
using MediaWorker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MediaWorker.Tests.Consumers;

public sealed class IngestConsumerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hmh-ingest-{Guid.NewGuid()}");
    private static readonly string ThumbnailsRoot = Path.GetFullPath("/thumbnails");

    public IngestConsumerTests()
    {
        Directory.CreateDirectory(_tempDir);
        try { Directory.CreateDirectory(ThumbnailsRoot); } catch { /* guarded per test */ }
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static bool ThumbnailsDirAvailable() => Directory.Exists(ThumbnailsRoot);

    private static ServiceProvider BuildProvider(Func<IServiceProvider, WorkerDbContext> dbFactory) =>
        new ServiceCollection()
            .AddSingleton<FileHashService>()
            .AddSingleton<FileConvertService>()
            .AddSingleton<MetadataService>()
            .AddSingleton<ThumbnailService>()
            .AddScoped<DedupService>()
            .AddScoped<IJobStore, JobStore>()
            .AddScoped(dbFactory)
            .AddMassTransitTestHarness(x => x.AddConsumer<IngestConsumer>())
            .BuildServiceProvider(true);

    private async Task<string> SaveJpegAsync(string name, int w = 32, int h = 32)
    {
        var path = Path.Combine(_tempDir, name);
        using var image = new Image<Rgba32>(w, h);
        await image.SaveAsJpegAsync(path);
        return path;
    }

    [Fact]
    public async Task DuplicateByHash_MarksJobSkippedDuplicate()
    {
        var dbName = Guid.NewGuid().ToString();
        var file = await SaveJpegAsync("dup.jpg");
        var hash = await new FileHashService().HashAsync(file, CancellationToken.None);

        var dupId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        using (var seed = TestDbContextFactory.CreateShared(dbName))
        {
            seed.MediaFiles.Add(new MediaFile { Id = dupId, FilePath = "/photo/original.jpg", FileHash = hash });
            seed.ProcessingJobs.Add(new ProcessingJob { Id = jobId, FilePath = file, Status = "queued" });
            await seed.SaveChangesAsync();
        }

        await using var provider = BuildProvider(_ => TestDbContextFactory.CreateShared(dbName));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new FileDiscovered(jobId, Guid.NewGuid(), file, "iphone", false, DateTimeOffset.UtcNow));
        Assert.True(await harness.Consumed.Any<FileDiscovered>());

        using var check = TestDbContextFactory.CreateShared(dbName);
        var job = await check.ProcessingJobs.FirstAsync(j => j.Id == jobId);
        Assert.Equal("skipped_duplicate", job.Status);
        Assert.Equal(dupId, job.MediaFileId);

        await harness.Stop();
    }

    [Fact]
    public async Task MissingFile_PublishesMediaProcessingFailed()
    {
        var dbName = Guid.NewGuid().ToString();
        var jobId = Guid.NewGuid();
        var missing = Path.Combine(_tempDir, "missing.jpg");
        using (var seed = TestDbContextFactory.CreateShared(dbName))
        {
            seed.ProcessingJobs.Add(new ProcessingJob { Id = jobId, FilePath = missing, Status = "queued" });
            await seed.SaveChangesAsync();
        }

        await using var provider = BuildProvider(_ => TestDbContextFactory.CreateShared(dbName));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new FileDiscovered(jobId, Guid.NewGuid(), missing, "iphone", false, DateTimeOffset.UtcNow));

        Assert.True(await harness.Published.Any<MediaProcessingFailed>(p =>
            p.Context.Message.JobId == jobId && p.Context.Message.Stage == "ingest"));

        await harness.Stop();
    }

    [Fact]
    public async Task UniqueViolationOnSave_MarksJobSkippedDuplicate()
    {
        if (!ThumbnailsDirAvailable()) return; // ingest reaches the save step only after thumbnailing
        var dbName = Guid.NewGuid().ToString();
        var file = await SaveJpegAsync("race.jpg");
        var jobId = Guid.NewGuid();
        using (var seed = new ThrowingWorkerDbContext(dbName))
        {
            seed.ProcessingJobs.Add(new ProcessingJob { Id = jobId, FilePath = file, Status = "queued" });
            await seed.SaveChangesAsync();
        }

        await using var provider = BuildProvider(_ => new ThrowingWorkerDbContext(dbName));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new FileDiscovered(jobId, Guid.NewGuid(), file, "iphone", false, DateTimeOffset.UtcNow));
        Assert.True(await harness.Consumed.Any<FileDiscovered>());

        using var check = new ThrowingWorkerDbContext(dbName);
        var job = await check.ProcessingJobs.FirstAsync(j => j.Id == jobId);
        Assert.Equal("skipped_duplicate", job.Status);

        await harness.Stop();
    }

    [Fact]
    public async Task Reprocess_ExistingRow_GoesStraightToEnrichment()
    {
        if (!ThumbnailsDirAvailable()) return; // reenrich may regenerate the thumbnail
        var dbName = Guid.NewGuid().ToString();
        var file = await SaveJpegAsync("existing.jpg");
        var mediaId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        using (var seed = TestDbContextFactory.CreateShared(dbName))
        {
            seed.MediaFiles.Add(new MediaFile { Id = mediaId, FilePath = file, FileHash = "abc" });
            seed.ProcessingJobs.Add(new ProcessingJob { Id = jobId, FilePath = file, Status = "queued" });
            await seed.SaveChangesAsync();
        }

        await using var provider = BuildProvider(_ => TestDbContextFactory.CreateShared(dbName));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new FileDiscovered(jobId, Guid.NewGuid(), file, "iphone", true, DateTimeOffset.UtcNow));

        Assert.True(await harness.Published.Any<MediaEnrichmentRequested>(p =>
            p.Context.Message.CorrelationId == mediaId));

        using var check = TestDbContextFactory.CreateShared(dbName);
        var job = await check.ProcessingJobs.FirstAsync(j => j.Id == jobId);
        Assert.Equal("enriching", job.Status);
        Assert.Equal(mediaId, job.MediaFileId);

        await harness.Stop();
    }
}
