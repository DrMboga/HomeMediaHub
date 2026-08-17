using Api.Features.PhotoOfTheDay;
using Api.Services;

namespace Api.Tests.Services;

/// <summary>
/// <c>NextRunTime</c> is <c>internal static</c> for exactly this reason — the timezone and
/// rollover arithmetic is the part worth testing, and it needs no host.
/// </summary>
public sealed class PhotoOfTheDaySchedulerTests
{
    // Berlin is the default: it is CET (UTC+1) in winter and CEST (UTC+2) in summer, so a fixed
    // 15:00 local lands on a different UTC instant depending on the date. That is the whole point
    // of resolving the zone instead of hardcoding an offset the way ScanScheduler does.
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById(PotdConfig.DefaultTimeZoneId);

    private static readonly TimeSpan ThreePm = new(15, 0, 0);

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi = 0) =>
        new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Fact]
    public void NextRunTime_BeforeTodaysSlot_ReturnsToday()
    {
        // 09:00 UTC = 11:00 CEST — 15:00 today has not happened yet.
        var next = PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 8, 17, 9), Berlin, ThreePm);

        Assert.Equal(Utc(2026, 8, 17, 13), next);          // 15:00 CEST is 13:00 UTC
        Assert.Equal(15, TimeZoneInfo.ConvertTime(next, Berlin).Hour);
    }

    [Fact]
    public void NextRunTime_AfterTodaysSlot_RollsToTomorrow()
    {
        // 16:00 UTC = 18:00 CEST — today's slot is gone.
        var next = PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 8, 17, 16), Berlin, ThreePm);

        Assert.Equal(Utc(2026, 8, 18, 13), next);
    }

    [Fact]
    public void NextRunTime_ExactlyAtTheSlot_RollsToTomorrow()
    {
        // Strictly after, so the loop cannot fire twice for the same day on a zero-length delay.
        var next = PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 8, 17, 13), Berlin, ThreePm);

        Assert.Equal(Utc(2026, 8, 18, 13), next);
    }

    [Fact]
    public void NextRunTime_RollsOverMonthAndYear()
    {
        Assert.Equal(Utc(2026, 9, 1, 13), PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 8, 31, 16), Berlin, ThreePm));

        // 31 Dec 16:00 UTC is already 17:00 CET, so the next slot is 1 Jan — and in winter
        // 15:00 Berlin is 14:00 UTC, not 13:00.
        Assert.Equal(Utc(2027, 1, 1, 14), PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 12, 31, 16), Berlin, ThreePm));
    }

    [Fact]
    public void NextRunTime_HoldsTheLocalHourAcrossTheSpringDstBoundary()
    {
        // Europe/Berlin springs forward 02:00→03:00 on the last Sunday of March: 29 March 2026.
        // The UTC instant of "15:00 local" shifts by an hour across it; the local hour must not.
        var before = PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 3, 28, 16), Berlin, ThreePm);
        var after  = PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 3, 29, 16), Berlin, ThreePm);

        Assert.Equal(Utc(2026, 3, 29, 13), before);   // CEST already — 29 March is the transition day
        Assert.Equal(Utc(2026, 3, 30, 13), after);
        Assert.All([before, after], t => Assert.Equal(15, TimeZoneInfo.ConvertTime(t, Berlin).Hour));
    }

    [Fact]
    public void NextRunTime_HoldsTheLocalHourAcrossTheAutumnDstBoundary()
    {
        // Falls back 03:00→02:00 on 25 October 2026.
        var before = PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 10, 24, 16), Berlin, ThreePm);
        var after  = PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 10, 25, 16), Berlin, ThreePm);

        Assert.Equal(Utc(2026, 10, 25, 14), before);  // CET now — 15:00 local is 14:00 UTC
        Assert.Equal(Utc(2026, 10, 26, 14), after);
        Assert.All([before, after], t => Assert.Equal(15, TimeZoneInfo.ConvertTime(t, Berlin).Hour));
    }

    [Fact]
    public void NextRunTime_SendAtLocalInsideTheSpringForwardGap_NudgesForwardInsteadOfSkippingTheDay()
    {
        // 02:30 on 29 March 2026 does not exist in Berlin — the clock jumps 02:00→03:00. A naive
        // implementation would produce an invalid DateTimeOffset or drop the day entirely.
        var gap  = new TimeSpan(2, 30, 0);
        var next = PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 3, 28, 12), Berlin, gap);

        var local = TimeZoneInfo.ConvertTime(next, Berlin);
        Assert.Equal(new DateTime(2026, 3, 29), local.Date);
        Assert.Equal(3, local.Hour);                  // nudged to the first instant that exists
        Assert.False(Berlin.IsInvalidTime(local.DateTime));
    }

    [Fact]
    public void NextRunTime_HonoursANonDefaultSendTime()
    {
        var next = PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 8, 17, 4), Berlin, new TimeSpan(7, 30, 0));

        Assert.Equal(Utc(2026, 8, 17, 5, 30), next);   // 07:30 CEST
    }

    [Fact]
    public void NextRunTime_InUtcZone_IsTheLiteralHour()
    {
        var utc  = TimeZoneInfo.Utc;
        var next = PhotoOfTheDayScheduler.NextRunTime(Utc(2026, 8, 17, 9), utc, ThreePm);

        Assert.Equal(Utc(2026, 8, 17, 15), next);
    }
}
