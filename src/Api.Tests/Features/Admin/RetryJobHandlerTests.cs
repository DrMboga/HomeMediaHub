using Api.Data;
using Api.Features.Admin;
using Contracts.Messages;
using Data.Entities;
using MassTransit;
using NSubstitute;

namespace Api.Tests.Features.Admin;

public sealed class RetryJobHandlerTests : IDisposable
{
    private readonly AppDbContext _db = TestDbContextFactory.Create();
    private readonly IPublishEndpoint _publish = Substitute.For<IPublishEndpoint>();

    public void Dispose() => _db.Dispose();

    private RetryJobHandler Handler() => new(_db, _publish);

    private static ProcessingJob FailedJob(Guid scanId) => new()
    {
        Id         = Guid.NewGuid(),
        FilePath   = "/photos/broken.jpg",
        Source     = "manual",
        Status     = "failed",
        ScanId     = scanId,
        StartedAt  = DateTime.UtcNow.AddMinutes(-5),
        FailedStep = "ingest",
        RetryCount = 1,
    };

    [Fact]
    public async Task Handle_FailedJob_ResetsToQueuedAndPublishesReprocess()
    {
        var scanId = Guid.NewGuid();
        var job    = FailedJob(scanId);
        _db.ProcessingJobs.Add(job);
        await _db.SaveChangesAsync();

        var ok = await Handler().Handle(new RetryJobRequest(job.Id), default);

        Assert.True(ok);
        await _publish.Received(1).Publish(
            Arg.Is<FileDiscovered>(m =>
                m.JobId == job.Id &&
                m.ScanId == scanId &&
                m.FilePath == job.FilePath &&
                m.Reprocess == true),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NonFailedJob_ReturnsFalseAndDoesNotPublish()
    {
        var job = FailedJob(Guid.NewGuid());
        job.Status = "completed";
        _db.ProcessingJobs.Add(job);
        await _db.SaveChangesAsync();

        var ok = await Handler().Handle(new RetryJobRequest(job.Id), default);

        Assert.False(ok);
        await _publish.DidNotReceive().Publish(Arg.Any<FileDiscovered>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnknownJob_ReturnsFalse()
    {
        var ok = await Handler().Handle(new RetryJobRequest(Guid.NewGuid()), default);

        Assert.False(ok);
    }
}
