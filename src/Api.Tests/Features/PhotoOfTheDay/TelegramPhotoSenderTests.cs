using Api.Features.PhotoOfTheDay;

namespace Api.Tests.Features.PhotoOfTheDay;

public sealed class TelegramPhotoSenderTests
{
    private static readonly Uri TelegramBase = new("https://api.telegram.org/");

    // A real bot token is {id}:{secret}. The colon is the whole problem — see BuildSendPhotoUri.
    private const string RealisticToken = "7088711346:AAHrExampleTokenValueNotARealSecret";

    [Fact]
    public void BuildSendPhotoUri_KeepsTheColonInThePathInsteadOfReadingItAsAScheme()
    {
        var uri = TelegramPhotoSender.BuildSendPhotoUri(TelegramBase, RealisticToken);

        // The prod failure was "The 'bot7088711346' scheme is not supported": the token's colon
        // made Uri parse the whole string as absolute and drop the base address entirely.
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("api.telegram.org", uri.Host);
        Assert.Equal($"https://api.telegram.org/bot{RealisticToken}/sendPhoto", uri.AbsoluteUri);
    }

    [Fact]
    public void BuildSendPhotoUri_DoesNotEscapeTheColon()
    {
        // Colons are legal in a path segment (RFC 3986 pchar). Telegram matches the token
        // literally, so %3A would 404.
        var uri = TelegramPhotoSender.BuildSendPhotoUri(TelegramBase, RealisticToken);

        Assert.Contains(":AAHr", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("%3A", uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSendPhotoUri_IgnoresAnyPathOnTheBaseAddress()
    {
        // An absolute-path reference replaces the base path rather than appending to it — which
        // is what we want if the base address ever gains a trailing segment.
        var uri = TelegramPhotoSender.BuildSendPhotoUri(new Uri("https://api.telegram.org/unused/"), RealisticToken);

        Assert.Equal($"https://api.telegram.org/bot{RealisticToken}/sendPhoto", uri.AbsoluteUri);
    }

    [Fact]
    public void BuildSendPhotoUri_HandlesATokenWithoutAColon()
    {
        // Not a shape Telegram issues, but a truncated or placeholder value must still produce a
        // request that reaches Telegram and comes back as a clean 404 — not a scheme exception.
        var uri = TelegramPhotoSender.BuildSendPhotoUri(TelegramBase, "placeholder");

        Assert.Equal("https://api.telegram.org/botplaceholder/sendPhoto", uri.AbsoluteUri);
    }

    // ── Redaction ─────────────────────────────────────────────────────────
    //
    // skippedReason is rendered on the Admin page. Nothing we construct puts the token in it, but
    // the catch around the send is deliberately broad, so the message can come from anywhere.

    [Fact]
    public void Redact_RemovesTheWholeToken()
    {
        var message = SendPhotoOfTheDayHandler.Redact(
            $"Something went wrong calling https://api.telegram.org/bot{RealisticToken}/sendPhoto",
            RealisticToken);

        Assert.DoesNotContain(RealisticToken, message, StringComparison.Ordinal);
        Assert.Contains("***", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RemovesEitherHalfOnItsOwn()
    {
        // The shape of the actual prod failure: the colon split the token and only the id half
        // appeared in the message.
        var message = SendPhotoOfTheDayHandler.Redact(
            "The 'bot7088711346' scheme is not supported.", RealisticToken);

        Assert.DoesNotContain("7088711346", message, StringComparison.Ordinal);

        var secret = RealisticToken[(RealisticToken.IndexOf(':') + 1)..];
        Assert.DoesNotContain(secret, SendPhotoOfTheDayHandler.Redact($"leaked {secret}", RealisticToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_LeavesAnOrdinaryMessageAlone()
    {
        const string clean = "No such host is known. (api.telegram.org:443)";

        Assert.Equal(clean, SendPhotoOfTheDayHandler.Redact(clean, RealisticToken));
    }
}
