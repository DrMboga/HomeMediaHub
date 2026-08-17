using System.Globalization;

namespace Api.Features.PhotoOfTheDay;

/// <summary>
/// Reads the <c>Potd:*</c> settings. Every getter treats <b>blank as absent</b>: compose
/// substitutes an empty string for a variable missing from the env file, and reading those
/// values naively is exactly what made the <c>RABBITMQ_*</c> vars dead in Program.cs.
/// </summary>
public static class PotdConfig
{
    /// <summary>
    /// 63,336 of the library's addressed photos are in Germany — more than the next three
    /// countries together — so that is where 15:00 means 15:00. Override with <c>POTD_TIMEZONE</c>.
    /// </summary>
    public const string DefaultTimeZoneId = "Europe/Berlin";

    public static readonly TimeSpan DefaultSendAtLocal = new(15, 0, 0);

    /// <summary>
    /// Off unless explicitly true — stage and prod share ports and one compose file, and stage
    /// must never post to the family channel. Only the scheduler honours this; the admin endpoint
    /// deliberately ignores it (a button press is not an accident).
    /// </summary>
    public static bool IsEnabled(IConfiguration config) =>
        bool.TryParse(Blank(config["Potd:Enabled"]), out var enabled) && enabled;

    public static string? BotToken(IConfiguration config) => Blank(config["Potd:BotToken"]);

    public static string? ChatId(IConfiguration config) => Blank(config["Potd:ChatId"]);

    /// <summary>Throws <see cref="TimeZoneNotFoundException"/> with the offending id if tzdata cannot resolve it.</summary>
    public static TimeZoneInfo TimeZone(IConfiguration config) =>
        TimeZoneInfo.FindSystemTimeZoneById(Blank(config["Potd:TimeZone"]) ?? DefaultTimeZoneId);

    /// <summary>Parses <c>Potd:SendAtLocal</c> as <c>HH:mm</c>, falling back to 15:00.</summary>
    public static TimeSpan SendAtLocal(IConfiguration config)
    {
        var raw = Blank(config["Potd:SendAtLocal"]);
        if (raw is null) return DefaultSendAtLocal;

        return TimeSpan.TryParseExact(raw, [@"hh\:mm", @"h\:mm", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : DefaultSendAtLocal;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
