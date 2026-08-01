using Contracts.Messages;
using MassTransit;

namespace Api.Services;

/// <summary>
/// Publishes <see cref="ScanRequested"/> at 02:00 UTC on the 1st of every month. Off by default
/// (<c>Scan:Enabled=false</c>) so stage doesn't kick off an unattended archive scan.
/// </summary>
public sealed class ScanScheduler : BackgroundService
{
    private readonly IPublishEndpoint _publish;
    private readonly IConfiguration _config;
    private readonly ILogger<ScanScheduler> _log;

    public ScanScheduler(IPublishEndpoint publish, IConfiguration config, ILogger<ScanScheduler> log)
    {
        _publish = publish;
        _config  = config;
        _log     = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Scan:Enabled", false))
        {
            _log.LogInformation("ScanScheduler disabled (Scan:Enabled=false)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var next  = NextRunTime(DateTimeOffset.UtcNow);
            var delay = next - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);

            var path = _config["Scan:DefaultPath"] ?? "/photo";
            await _publish.Publish(new ScanRequested(
                ScanId:      Guid.NewGuid(),
                Path:        path,
                Source:      "scheduled",
                TriggeredBy: "scheduler",
                RequestedAt: DateTimeOffset.UtcNow), stoppingToken);

            _log.LogInformation("ScanScheduler: published ScanRequested for {Path}", path);
        }
    }

    internal static DateTimeOffset NextRunTime(DateTimeOffset from)
    {
        var firstOfThisMonth = new DateTimeOffset(from.Year, from.Month, 1, 2, 0, 0, TimeSpan.Zero);
        return from < firstOfThisMonth ? firstOfThisMonth : firstOfThisMonth.AddMonths(1);
    }
}
