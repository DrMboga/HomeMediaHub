namespace Data.Entities;

/// <summary>
/// One row per day the scheduler posted a Photo of the Day. <see cref="SentOn"/> is the primary
/// key, so the insert itself is the "already sent today" guard — a 23505 means another run (or a
/// restart across the 15:00 boundary) already claimed the day.
/// </summary>
/// <remarks>
/// Only <c>PhotoOfTheDayScheduler</c> writes here. The manual
/// <c>POST /api/admin/potd/send</c> deliberately does not, so a test send at 11:00 cannot make
/// the real run skip the day.
/// </remarks>
public sealed class PotdSend
{
    /// <summary>The local calendar date in <c>Potd:TimeZone</c> — not UTC, and not the photo's date.</summary>
    public DateOnly SentOn { get; set; }

    /// <summary>
    /// Which photo went out. Deliberately not an FK: the row is a send log, and it must survive
    /// the photo being deleted from the library later.
    /// </summary>
    public Guid MediaFileId { get; set; }

    public DateTime SentAt { get; set; }
}
