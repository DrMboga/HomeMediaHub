using System.Net;
using System.Text;
using GeoService.Data;
using GeoService.Features.Geocoding;
using GeoService.Models;
using GeoService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace GeoService.Tests.Features.Geocoding;

// Cache-miss tests call the real NominatimClient which enforces a 1 req/sec rate limit.
// Each cache-miss test therefore takes ~1 second — this is by design.
public sealed class ReverseGeocodeHandlerTests
{
    private static GeoDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<GeoDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static NominatimClient CreateNominatimClient(string? address = "Test Address, London")
    {
        var json = address is null
            ? "{}"
            : $$"""{"display_name":"{{address}}"}""";

        var handler = new FakeHttpHandler(HttpStatusCode.OK, json);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://nominatim.openstreetmap.org/")
        };
        var logger = Substitute.For<ILogger<NominatimClient>>();
        return new NominatimClient(httpClient, logger);
    }

    [Fact]
    public async Task Handle_CacheHit_ReturnsCachedAddressWithoutCallingNominatim()
    {
        using var db = CreateDb();
        db.GeoCacheEntries.Add(new GeoCacheEntry { Lat = 51.5, Lon = -0.1167, Address = "London, UK" });
        await db.SaveChangesAsync();

        // NominatimClient with a handler that always fails — proves it's never called
        var httpClient = new HttpClient(new FakeHttpHandler(HttpStatusCode.ServiceUnavailable, ""))
        {
            BaseAddress = new Uri("https://nominatim.openstreetmap.org/")
        };
        var nominatim = new NominatimClient(httpClient, Substitute.For<ILogger<NominatimClient>>());
        var handler = new ReverseGeocodeHandler(db, nominatim);

        var result = await handler.Handle(new ReverseGeocodeRequest(51.5, -0.1167), CancellationToken.None);

        Assert.Equal("London, UK", result.Address);
        Assert.True(result.Cached);
    }

    [Fact]
    public async Task Handle_CacheHit_CoordinatesMatchOnRoundedKey()
    {
        // Rounded to 4 decimal places: 51.12345678 → 51.1235
        using var db = CreateDb();
        db.GeoCacheEntries.Add(new GeoCacheEntry { Lat = 51.1235, Lon = -0.1167, Address = "Cached" });
        await db.SaveChangesAsync();

        var handler = new ReverseGeocodeHandler(db, CreateNominatimClient());

        var result = await handler.Handle(new ReverseGeocodeRequest(51.12349, -0.11674), CancellationToken.None);

        Assert.True(result.Cached);
        Assert.Equal("Cached", result.Address);
    }

    [Fact]
    public async Task Handle_CacheMiss_CallsNominatimAndReturnsAddress()
    {
        using var db = CreateDb();
        var handler = new ReverseGeocodeHandler(db, CreateNominatimClient("Test Address, London"));

        var result = await handler.Handle(new ReverseGeocodeRequest(51.5, -0.1167), CancellationToken.None);

        Assert.Equal("Test Address, London", result.Address);
        Assert.False(result.Cached);
    }

    [Fact]
    public async Task Handle_CacheMiss_PersistsResultToCache()
    {
        using var db = CreateDb();
        var handler = new ReverseGeocodeHandler(db, CreateNominatimClient("Persisted Address"));

        await handler.Handle(new ReverseGeocodeRequest(51.5, -0.1167), CancellationToken.None);

        var entry = await db.GeoCacheEntries
            .FirstOrDefaultAsync(e => e.Lat == 51.5 && e.Lon == -0.1167);
        Assert.NotNull(entry);
        Assert.Equal("Persisted Address", entry.Address);
    }

    [Fact]
    public async Task Handle_NominatimReturnsNull_DoesNotPersistToCache()
    {
        using var db = CreateDb();
        var handler = new ReverseGeocodeHandler(db, CreateNominatimClient(address: null));

        var result = await handler.Handle(new ReverseGeocodeRequest(51.5, -0.1167), CancellationToken.None);

        Assert.Null(result.Address);
        Assert.False(result.Cached);
        var count = await db.GeoCacheEntries.CountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Handle_CoordinatesAreRoundedTo4Decimals()
    {
        using var db = CreateDb();
        db.GeoCacheEntries.Add(new GeoCacheEntry { Lat = 51.1235, Lon = -0.1167, Address = "Rounded" });
        await db.SaveChangesAsync();

        var handler = new ReverseGeocodeHandler(db, CreateNominatimClient());

        // These round to 51.1235, -0.1167 — should hit cache despite extra precision
        var result = await handler.Handle(new ReverseGeocodeRequest(51.12354, -0.11674), CancellationToken.None);

        Assert.True(result.Cached);
    }

    private sealed class FakeHttpHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }
}
