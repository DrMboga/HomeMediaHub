using System.Globalization;
using Api.Data;
using Api.Features.Media.DisplayMedia;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Api.Features.PhotoOfTheDay;

/// <param name="Month">Optional — defaults to today in <c>Potd:TimeZone</c>.</param>
/// <param name="Day">Optional — see <paramref name="Month"/>.</param>
/// <param name="MediaFileId">Optional — skip the pick and send this exact photo.</param>
/// <param name="DryRun">Run the pick and build the caption, but do not call Telegram.</param>
public record SendPhotoOfTheDayRequest(
    int? Month = null, int? Day = null, Guid? MediaFileId = null, bool DryRun = false)
    : IRequest<SendPhotoOfTheDayResult>;

public record SendPhotoOfTheDayResult(
    bool Sent, string? Tier, Guid? MediaFileId, string? FilePath, string? Caption, string? SkippedReason);

/// <summary>
/// Picks (or takes) a photo, builds the caption, and posts it. Shared by the scheduler and by
/// <c>POST /api/admin/potd/send</c> — the difference between them lives entirely in the callers:
/// the scheduler gates on <c>Potd:Enabled</c>, checks the already-sent guard, and records the
/// send; this handler does none of those things.
/// </summary>
public sealed class SendPhotoOfTheDayHandler : IRequestHandler<SendPhotoOfTheDayRequest, SendPhotoOfTheDayResult>
{
    /// <summary>Telegram's <c>sendPhoto</c> upload cap.</summary>
    private const long MaxUploadBytes = 10L * 1024 * 1024;

    /// <summary>Telegram also rejects photos whose width + height exceed this.</summary>
    private const int MaxCombinedDimension = 10_000;

    /// <summary>What an oversized photo is resized to fit. Comfortably inside both caps at q85.</summary>
    private const int DownscaleBox = 2560;

    private readonly AppDbContext _db;
    private readonly IMediator _mediator;
    private readonly TelegramPhotoSender _telegram;
    private readonly IConfiguration _config;
    private readonly ILogger<SendPhotoOfTheDayHandler> _log;

    public SendPhotoOfTheDayHandler(
        AppDbContext db, IMediator mediator, TelegramPhotoSender telegram,
        IConfiguration config, ILogger<SendPhotoOfTheDayHandler> log)
    {
        _db       = db;
        _mediator = mediator;
        _telegram = telegram;
        _config   = config;
        _log      = log;
    }

    public async Task<SendPhotoOfTheDayResult> Handle(SendPhotoOfTheDayRequest req, CancellationToken ct)
    {
        // ── which photo ────────────────────────────────────────────────────
        Guid mediaFileId;
        string tier;

        if (req.MediaFileId is { } explicitId)
        {
            mediaFileId = explicitId;
            tier        = PotdTier.Explicit;
        }
        else
        {
            var (month, day) = ResolveCalendarDay(req.Month, req.Day);
            var pick = await _mediator.Send(new PickPhotoOfTheDayRequest(month, day), ct);
            if (pick is null)
            {
                return new SendPhotoOfTheDayResult(false, null, null, null, null,
                    $"No postable photo for {day:00}.{month:00} in any tier");
            }

            mediaFileId = pick.MediaFileId;
            tier        = pick.Tier;
        }

        // ── caption ────────────────────────────────────────────────────────
        var details = await _db.MediaFiles
            .Where(f => f.Id == mediaFileId && !f.IsDeleted)
            .Select(f => new { f.TakenAt, Address = f.Metadata!.Address })
            .FirstOrDefaultAsync(ct);

        if (details is null)
            return new SendPhotoOfTheDayResult(false, tier, mediaFileId, null, null, "Media file not found");

        var caption = BuildCaption(details.TakenAt, details.Address);

        // ── the image ──────────────────────────────────────────────────────
        // DisplayMediaRequest hands back a path plus content type, already handling HEIC→JPEG and
        // caching. Preferred over the stored WebP thumbnail: higher quality, and Telegram treats
        // WebP inconsistently. Sent in-process — the Api never calls its own HTTP endpoint.
        var displayCacheDir = _config["DisplayCacheDir"] ?? "/display-cache";
        var display = await _mediator.Send(new DisplayMediaRequest(mediaFileId, displayCacheDir), ct);

        if (display is null)
        {
            return new SendPhotoOfTheDayResult(false, tier, mediaFileId, null, caption,
                "Could not produce a displayable image (missing file, or heif-convert failed)");
        }

        // The pick excludes videos, but an explicit mediaFileId does not go through the pick.
        // Caught here so the answer names the real problem instead of surfacing as an ImageSharp
        // decode failure below, or as a Telegram rejection.
        if (!display.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new SendPhotoOfTheDayResult(false, tier, mediaFileId, display.FilePath, caption,
                $"Not an image ({display.ContentType}) — sendPhoto takes a still");
        }

        string filePath, contentType;
        try
        {
            (filePath, contentType) = await FitForTelegramAsync(display, mediaFileId, displayCacheDir, ct);
        }
        catch (Exception ex) when (ex is ImageFormatException or IOException or UnknownImageFormatException)
        {
            _log.LogError(ex, "Photo of the Day: could not downscale {MediaFileId} for Telegram", mediaFileId);
            return new SendPhotoOfTheDayResult(false, tier, mediaFileId, display.FilePath, caption,
                $"Image too large for Telegram and downscaling failed: {ex.Message}");
        }

        // ── send ───────────────────────────────────────────────────────────
        if (req.DryRun)
            return new SendPhotoOfTheDayResult(false, tier, mediaFileId, filePath, caption, "dryRun");

        var botToken = PotdConfig.BotToken(_config);
        var chatId   = PotdConfig.ChatId(_config);

        // Configuration, not a runtime failure — say which key is missing rather than
        // null-referencing inside the HTTP call.
        if (botToken is null || chatId is null)
        {
            var missing = botToken is null && chatId is null ? "Potd:BotToken and Potd:ChatId"
                        : botToken is null                   ? "Potd:BotToken"
                        :                                      "Potd:ChatId";
            return new SendPhotoOfTheDayResult(false, tier, mediaFileId, filePath, caption,
                $"{missing} not configured");
        }

        try
        {
            await _telegram.SendPhotoAsync(botToken, chatId, filePath, contentType, caption, ct);
        }
        // Deliberately broad. The point of this handler is that a failure is diagnosable from the
        // response — "which tier, which photo, what went wrong" — instead of a 500 and a stack
        // trace in the container log. An allow-list of exception types was too narrow the first
        // time out: a malformed request URI throws NotSupportedException, which escaped.
        // Cancellation of the caller's own token is the one thing that must still propagate.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Photo of the Day: Telegram send failed for {MediaFileId}", mediaFileId);
            return new SendPhotoOfTheDayResult(false, tier, mediaFileId, filePath, caption,
                Redact(ex.Message, botToken));
        }

        return new SendPhotoOfTheDayResult(true, tier, mediaFileId, filePath, caption, null);
    }

    /// <summary>
    /// Keeps the bot token out of <c>skippedReason</c>, which is rendered on the Admin page and is
    /// the sort of string that ends up pasted into a bug report.
    /// </summary>
    /// <remarks>
    /// Nothing we construct includes the token, but the catch around the send is deliberately
    /// broad, so the message can come from anywhere — and the first prod failure was an exception
    /// that quoted part of the URL back. Also redacts the id half on its own: a token split on its
    /// colon is exactly how that message was shaped.
    /// </remarks>
    internal static string Redact(string message, string botToken)
    {
        message = message.Replace(botToken, "***", StringComparison.Ordinal);

        var colon = botToken.IndexOf(':');
        if (colon > 0)
        {
            message = message.Replace(botToken[..colon], "***", StringComparison.Ordinal);
            message = message.Replace(botToken[(colon + 1)..], "***", StringComparison.Ordinal);
        }

        return message;
    }

    /// <summary>Today's month/day in <c>Potd:TimeZone</c>, unless the caller named one.</summary>
    private (int Month, int Day) ResolveCalendarDay(int? month, int? day)
    {
        if (month is > 0 and <= 12 && day is > 0 and <= 31)
            return (month.Value, day.Value);

        var tz    = PotdConfig.TimeZone(_config);
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        return (local.Month, local.Day);
    }

    /// <summary>
    /// "17 August 2019 · Berlin, Deutschland". <c>Address</c> is already one formatted string —
    /// unlike the old Pi version, which assembled locality/region/country separately.
    /// </summary>
    internal static string BuildCaption(DateTime? takenAt, string? address)
    {
        var parts = new List<string>(2);
        if (takenAt is { } t) parts.Add(t.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")));
        if (!string.IsNullOrWhiteSpace(address)) parts.Add(address.Trim());
        return parts.Count == 0 ? "Photo of the Day" : string.Join(" · ", parts);
    }

    /// <summary>
    /// Checks the file against Telegram's caps and downscales only if it breaches one. The
    /// header-only <c>Identify</c> keeps the common case (a q90 JPEG off a 12 MP iPhone HEIC,
    /// well inside both limits) free of a full decode.
    /// </summary>
    private async Task<(string FilePath, string ContentType)> FitForTelegramAsync(
        DisplayMediaResult display, Guid mediaFileId, string displayCacheDir, CancellationToken ct)
    {
        var info = new FileInfo(display.FilePath);
        var image = await Image.IdentifyAsync(display.FilePath, ct);

        var tooBig = info.Length > MaxUploadBytes ||
                     image.Width + image.Height > MaxCombinedDimension;

        if (!tooBig) return (display.FilePath, display.ContentType);

        // Cached beside the display JPEGs so a re-send costs nothing. Distinct suffix so it can
        // never collide with DisplayMediaHandler's own {id}.jpg.
        var resizedPath = Path.Combine(displayCacheDir, $"{mediaFileId}.potd.jpg");
        if (File.Exists(resizedPath)) return (resizedPath, "image/jpeg");

        _log.LogInformation(
            "Photo of the Day: downscaling {MediaFileId} for Telegram ({Bytes} bytes, {Width}x{Height})",
            mediaFileId, info.Length, image.Width, image.Height);

        Directory.CreateDirectory(displayCacheDir);
        var tempPath = Path.Combine(displayCacheDir, $"{mediaFileId}.{Guid.NewGuid():N}.part.jpg");

        using (var full = await Image.LoadAsync(display.FilePath, ct))
        {
            full.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(DownscaleBox, DownscaleBox),
            }));
            await full.SaveAsJpegAsync(tempPath, new JpegEncoder { Quality = 85 }, ct);
        }

        // Move-on-success, same reasoning as DisplayMediaHandler: the cache path is read straight
        // off disk next time, so a partial file must never be left there.
        File.Move(tempPath, resizedPath, overwrite: true);
        return (resizedPath, "image/jpeg");
    }
}
