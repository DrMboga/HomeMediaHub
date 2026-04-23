using System.Net;
using System.Text;
using GeoService.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace GeoService.Tests.Services;

// Each test acquires the static 1 req/sec semaphore — expect ~1 second per test.
public sealed class NominatimClientTests
{
    private static NominatimClient Create(HttpStatusCode statusCode, string responseBody)
    {
        var handler = new FakeHttpHandler(statusCode, responseBody);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://nominatim.openstreetmap.org/")
        };
        return new NominatimClient(httpClient, Substitute.For<ILogger<NominatimClient>>());
    }

    [Fact]
    public async Task ReverseGeocodeAsync_ValidResponse_ReturnsDisplayName()
    {
        var client = Create(HttpStatusCode.OK, """{"display_name":"10 Downing Street, London, UK"}""");

        var result = await client.ReverseGeocodeAsync(51.5034, -0.1276);

        Assert.Equal("10 Downing Street, London, UK", result);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_ResponseMissingDisplayName_ReturnsNull()
    {
        var client = Create(HttpStatusCode.OK, "{}");

        var result = await client.ReverseGeocodeAsync(0.0, 0.0);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_HttpError_ReturnsNull()
    {
        var client = Create(HttpStatusCode.ServiceUnavailable, "Service unavailable");

        var result = await client.ReverseGeocodeAsync(51.5, -0.1);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_FormatsCoordinatesTo4DecimalPlaces()
    {
        string? capturedUrl = null;
        var handler = new CapturingHttpHandler(req =>
        {
            capturedUrl = req.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://nominatim.openstreetmap.org/")
        };
        var client = new NominatimClient(httpClient, Substitute.For<ILogger<NominatimClient>>());

        await client.ReverseGeocodeAsync(51.123456, -0.987654);

        Assert.NotNull(capturedUrl);
        Assert.Contains("lat=51.1235", capturedUrl);
        Assert.Contains("lon=-0.9877", capturedUrl);
    }

    private sealed class FakeHttpHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }

    private sealed class CapturingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
