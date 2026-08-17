using System.Net.Http.Headers;
using System.Text.Json;

namespace Api.Features.PhotoOfTheDay;

/// <summary>
/// The Api's only outbound internet call. The stack is LAN-only for <i>inbound</i>; the container
/// already has egress (GeoService reaches Nominatim), but nothing in <c>api</c> had used it before
/// Phase 11 — worth stating, since "LAN-only" appears throughout the docs.
/// </summary>
public sealed class TelegramPhotoSender
{
    public const string HttpClientName = "TelegramBotApi";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TelegramPhotoSender> _log;

    public TelegramPhotoSender(IHttpClientFactory httpFactory, ILogger<TelegramPhotoSender> log)
    {
        _httpFactory = httpFactory;
        _log         = log;
    }

    /// <summary>
    /// <c>POST bot{token}/sendPhoto</c> as multipart. Throws <see cref="InvalidOperationException"/>
    /// on a non-2xx or an <c>ok:false</c> body — the caller turns that into a skip reason so a bad
    /// token is diagnosable from the admin response instead of the logs.
    /// </summary>
    public async Task SendPhotoAsync(
        string botToken, string chatId, string filePath, string contentType, string caption, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(chatId), "chat_id");
        content.Add(new StringContent(caption), "caption");

        await using var file = File.OpenRead(filePath);
        var photo = new StreamContent(file);
        photo.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(photo, "photo", Path.GetFileName(filePath));

        var client = _httpFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsync($"bot{botToken}/sendPhoto", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // The token is in the URL, never in the message — this string reaches the admin UI.
            throw new InvalidOperationException(
                $"Telegram sendPhoto returned {(int)response.StatusCode}: {Describe(body)}");
        }

        // Telegram answers 200 with {"ok":false,...} for things like a bad chat_id.
        if (!IsOk(body))
            throw new InvalidOperationException($"Telegram sendPhoto refused the request: {Describe(body)}");

        _log.LogInformation("Photo of the Day posted to Telegram chat {ChatId}", chatId);
    }

    private static bool IsOk(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch (JsonException)
        {
            return false;  // not JSON at all — treat as a failure and let Describe show it
        }
    }

    private static string Describe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("description", out var d) && d.GetString() is { } text)
                return text;
        }
        catch (JsonException) { /* fall through to the raw body */ }

        return body.Length > 300 ? body[..300] : body;
    }
}
