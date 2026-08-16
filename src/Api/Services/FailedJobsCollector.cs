using System.Diagnostics.Metrics;
using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Publishes the number of <c>processing_jobs</c> rows in <c>failed</c> state as an OTel gauge.
/// <para>
/// This is the one pipeline failure mode MassTransit's own metrics cannot see: per-file failures
/// are caught inside <c>IngestConsumer</c> and published as <c>MediaProcessingFailed</c> rather
/// than rethrown, so the broker records a <em>successful</em> consume and the "consume faults" and
/// "error queue depth" panels stay flat. All 171 failures of the prod archive import passed
/// unseen that way; only the Admin page showed them, and only if you went looking.
/// </para>
/// <para>
/// Sampled on a timer rather than inside the gauge callback: observable-instrument callbacks run
/// synchronously on the Prometheus scrape, and a database round-trip there would tie scrape
/// latency (and any DB stall) to the metrics endpoint.
/// </para>
/// </summary>
public sealed class FailedJobsCollector : BackgroundService
{
    public const string MeterName = "HomeMediaHub.Api";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FailedJobsCollector> _log;
    private readonly Meter _meter;

    // -1 = never sampled successfully. The gauge reports nothing until then: a gap in the graph is
    // honest about "we don't know", where a zero would read as "nothing is failing".
    private long _failedJobs = -1;

    public FailedJobsCollector(
        IServiceScopeFactory scopes, IMeterFactory meterFactory, ILogger<FailedJobsCollector> log)
    {
        _scopes = scopes;
        _log = log;
        _meter = meterFactory.Create(MeterName);

        _meter.CreateObservableGauge(
            "homemediahub.processing_jobs.failed",
            ObserveFailedJobs,
            description: "processing_jobs rows currently in the failed state");
    }

    private IEnumerable<Measurement<long>> ObserveFailedJobs()
    {
        var value = Interlocked.Read(ref _failedJobs);
        if (value < 0) yield break;
        yield return new Measurement<long>(value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                await SampleAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var count = await db.ProcessingJobs.CountAsync(j => j.Status == "failed", ct);
            Interlocked.Exchange(ref _failedJobs, count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep reporting the last known value rather than dropping to zero on a blip.
            _log.LogWarning(ex, "FailedJobsCollector: could not sample failed job count");
        }
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}
