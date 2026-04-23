using System.Diagnostics;
using System.Text.Json.Serialization;

namespace GeoService.Services;

public sealed class NominatimClient(HttpClient http, ILogger<NominatimClient> logger)
{
    // Shared across all instances — Nominatim policy: max 1 req/sec
    private static readonly SemaphoreSlim _throttle = new(1, 1);

    public async Task<string?> ReverseGeocodeAsync(double lat, double lon, CancellationToken ct = default)
    {
        await _throttle.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await http.GetFromJsonAsync<NominatimResponse>(
                FormattableString.Invariant($"reverse?lat={lat:F4}&lon={lon:F4}&format=jsonv2&addressdetails=0"),
                ct);

            logger.LogInformation("Nominatim {Lat},{Lon} → {Ms}ms hit={Hit}",
                lat, lon, sw.ElapsedMilliseconds, response is not null);

            // Pad to 1 000 ms minimum between requests before releasing the lock
            var remaining = 1000 - (int)sw.ElapsedMilliseconds;
            if (remaining > 0)
                await Task.Delay(remaining, ct);

            return response?.DisplayName;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Nominatim failed {Lat},{Lon} {Ms}ms", lat, lon, sw.ElapsedMilliseconds);

            // Enforce delay even on failure to avoid hammering Nominatim during retries
            var remaining = 1000 - (int)sw.ElapsedMilliseconds;
            if (remaining > 0)
                await Task.Delay(Math.Max(0, remaining), CancellationToken.None);

            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }
}

internal sealed record NominatimResponse(
    [property: JsonPropertyName("display_name")] string? DisplayName
);
