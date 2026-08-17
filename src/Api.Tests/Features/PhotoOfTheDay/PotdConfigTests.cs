using Api.Features.PhotoOfTheDay;
using Microsoft.Extensions.Configuration;

namespace Api.Tests.Features.PhotoOfTheDay;

public sealed class PotdConfigTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

    // ── Blank is absent ───────────────────────────────────────────────────
    //
    // compose substitutes an empty string for a variable missing from the env file. Reading
    // those values naively is what made the RABBITMQ_* vars dead in Program.cs; every getter
    // here has to treat "" the same as unset.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BotTokenAndChatId_TreatBlankAsAbsent(string? blank)
    {
        var config = Config(("Potd:BotToken", blank), ("Potd:ChatId", blank));

        Assert.Null(PotdConfig.BotToken(config));
        Assert.Null(PotdConfig.ChatId(config));
    }

    [Fact]
    public void BotTokenAndChatId_ReturnConfiguredValues()
    {
        var config = Config(("Potd:BotToken", "123:ABC"), ("Potd:ChatId", "-1001234567890"));

        Assert.Equal("123:ABC", PotdConfig.BotToken(config));
        Assert.Equal("-1001234567890", PotdConfig.ChatId(config));
    }

    // ── Enabled defaults off ──────────────────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("not-a-bool", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void IsEnabled_DefaultsOffAndOnlyTrueTurnsItOn(string? value, bool expected)
    {
        // Stage and prod share ports and one compose file; a malformed value must never be
        // read as "post to the family channel".
        Assert.Equal(expected, PotdConfig.IsEnabled(Config(("Potd:Enabled", value))));
    }

    // ── Send time ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("half past three")]
    public void SendAtLocal_FallsBackTo1500(string? value)
    {
        Assert.Equal(new TimeSpan(15, 0, 0), PotdConfig.SendAtLocal(Config(("Potd:SendAtLocal", value))));
    }

    [Theory]
    [InlineData("07:30", 7, 30)]
    [InlineData("7:30", 7, 30)]
    [InlineData("21:05", 21, 5)]
    public void SendAtLocal_ParsesHourMinute(string value, int hour, int minute)
    {
        Assert.Equal(new TimeSpan(hour, minute, 0), PotdConfig.SendAtLocal(Config(("Potd:SendAtLocal", value))));
    }

    // ── Time zone ─────────────────────────────────────────────────────────

    [Fact]
    public void TimeZone_DefaultsToBerlinAndResolvesFromTzdata()
    {
        // Also the smoke test that FindSystemTimeZoneById works at all on the host — the
        // container equivalent (tzdata + ICU present, InvariantGlobalization not set) is a
        // deploy-time check no unit test can make.
        Assert.Equal(PotdConfig.DefaultTimeZoneId, PotdConfig.TimeZone(Config()).Id);
        Assert.Equal(PotdConfig.DefaultTimeZoneId, PotdConfig.TimeZone(Config(("Potd:TimeZone", ""))).Id);
    }

    [Fact]
    public void TimeZone_HonoursAConfiguredZone()
    {
        Assert.Equal("UTC", PotdConfig.TimeZone(Config(("Potd:TimeZone", "UTC"))).Id);
    }

    [Fact]
    public void TimeZone_ThrowsOnAnUnknownZone()
    {
        // The scheduler catches this once, up front, and declines to schedule — a bad id is a
        // config error, not something to retry daily.
        Assert.Throws<TimeZoneNotFoundException>(() => PotdConfig.TimeZone(Config(("Potd:TimeZone", "Mars/Olympus_Mons"))));
    }
}
