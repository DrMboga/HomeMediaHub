using Api.Data;
using Api.Features.PhotoOfTheDay;
using Data.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Api.Services;

/// <summary>
/// Posts one Photo of the Day to Telegram at <c>Potd:SendAtLocal</c> (default 15:00) in
/// <c>Potd:TimeZone</c>. Off by default (<c>Potd:Enabled=false</c>) so stage never posts to the
/// family channel.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ScanScheduler"/>, with the four differences architecture.md §14 calls out:
/// a DI scope per run (<c>AppDbContext</c> is scoped, a BackgroundService is not), local time
/// rather than UTC, a try/catch around the per-run body (an escaped exception stops the whole Api
/// host — the default <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>), and a
/// <c>potd_sends</c> guard so a restart across the boundary does not re-send.
/// </remarks>
public sealed class PhotoOfTheDayScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<PhotoOfTheDayScheduler> _log;

    public PhotoOfTheDayScheduler(
        IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<PhotoOfTheDayScheduler> log)
    {
        _scopeFactory = scopeFactory;
        _config       = config;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!PotdConfig.IsEnabled(_config))
        {
            _log.LogInformation("PhotoOfTheDayScheduler disabled (Potd:Enabled=false)");
            return;
        }

        TimeZoneInfo tz;
        try
        {
            tz = PotdConfig.TimeZone(_config);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Resolved once, up front: a bad id is a config error, and retrying it every day
            // would just log the same failure forever.
            _log.LogError(ex, "PhotoOfTheDayScheduler: Potd:TimeZone '{TimeZone}' could not be resolved — not scheduling",
                _config["Potd:TimeZone"] ?? PotdConfig.DefaultTimeZoneId);
            return;
        }

        var sendAt = PotdConfig.SendAtLocal(_config);
        _log.LogInformation("PhotoOfTheDayScheduler enabled — {SendAt} {TimeZone}", sendAt, tz.Id);

        while (!stoppingToken.IsCancellationRequested)
        {
            var next  = NextRunTime(DateTimeOffset.UtcNow, tz, sendAt);
            var delay = next - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);

            try
            {
                await RunOnceAsync(tz, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A Telegram outage must not take the Api down with it.
                _log.LogError(ex, "PhotoOfTheDayScheduler: run failed");
            }
        }
    }

    private async Task RunOnceAsync(TimeZoneInfo tz, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db       = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        if (await db.PotdSends.AnyAsync(s => s.SentOn == today, ct))
        {
            _log.LogInformation("PhotoOfTheDayScheduler: {Date} already sent — skipping", today);
            return;
        }

        // Claim the day *before* sending, per architecture.md §14: the primary key is the guard,
        // and a 23505 means a concurrent run (or a restart across the boundary) got there first.
        db.PotdSends.Add(new PotdSend { SentOn = today, MediaFileId = Guid.Empty, SentAt = DateTime.UtcNow });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            _log.LogInformation("PhotoOfTheDayScheduler: {Date} claimed by another run — skipping", today);
            return;
        }

        SendPhotoOfTheDayResult result;
        try
        {
            result = await mediator.Send(new SendPhotoOfTheDayRequest(), ct);
        }
        catch
        {
            await ReleaseClaimAsync(db, today, CancellationToken.None);
            throw;
        }

        if (!result.Sent)
        {
            // Release the claim so the row never records a send that did not happen — and so a
            // restart before midnight retries the day instead of skipping it. The guard exists to
            // prevent a double *successful* send, not to burn a day on a Telegram outage.
            await ReleaseClaimAsync(db, today, ct);
            _log.LogError("PhotoOfTheDayScheduler: {Date} not sent — {Reason}", today, result.SkippedReason);
            return;
        }

        // Fill in which photo actually went out. The claim row was written before the pick ran,
        // so until now it holds Guid.Empty.
        await db.PotdSends
            .Where(s => s.SentOn == today)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.MediaFileId, result.MediaFileId!.Value)
                .SetProperty(x => x.SentAt, DateTime.UtcNow), ct);

        _log.LogInformation("PhotoOfTheDayScheduler: {Date} sent {MediaFileId} (tier {Tier})",
            today, result.MediaFileId, result.Tier);
    }

    private static Task ReleaseClaimAsync(AppDbContext db, DateOnly day, CancellationToken ct) =>
        db.PotdSends.Where(s => s.SentOn == day && s.MediaFileId == Guid.Empty).ExecuteDeleteAsync(ct);

    /// <summary>
    /// The next instant at which the wall clock in <paramref name="tz"/> reads
    /// <paramref name="sendAtLocal"/>, strictly after <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// Kept <c>internal static</c> for the same reason <see cref="ScanScheduler.NextRunTime"/> is:
    /// the timezone and rollover arithmetic is the part worth unit-testing, and it needs no host.
    /// </remarks>
    internal static DateTimeOffset NextRunTime(DateTimeOffset from, TimeZoneInfo tz, TimeSpan sendAtLocal)
    {
        var localDay = TimeZoneInfo.ConvertTime(from, tz).Date;

        // Two iterations is always enough — today's slot, else tomorrow's. The third is a guard
        // against a pathological zone, not an expected case.
        for (var offset = 0; offset < 3; offset++)
        {
            var candidate = ResolveLocal(localDay.AddDays(offset), sendAtLocal, tz);
            if (candidate > from) return candidate;
        }

        throw new InvalidOperationException($"Could not find a run time after {from:O} in {tz.Id}");
    }

    private static DateTimeOffset ResolveLocal(DateTime day, TimeSpan at, TimeZoneInfo tz)
    {
        var local = day.Date + at;

        // Spring-forward deletes a wall-clock hour outright. Nudge to the first instant that
        // exists rather than skipping the day. (15:00 is nowhere near a real DST transition;
        // this is here so an odd Potd:SendAtLocal cannot silently drop a post.)
        while (tz.IsInvalidTime(local))
            local = local.AddMinutes(15);

        // For an ambiguous local time (autumn fall-back) GetUtcOffset returns the standard-time
        // offset — the later of the two occurrences. Deterministic, and an hour either way does
        // not matter for a daily photo.
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }
}
