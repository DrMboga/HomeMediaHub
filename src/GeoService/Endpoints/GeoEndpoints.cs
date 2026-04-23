using GeoService.Features.Geocoding;
using MediatR;

namespace GeoService.Endpoints;

public static class GeoEndpoints
{
    public static void MapGeoEndpoints(this WebApplication app)
    {
        app.MapGet("/reverse", (double lat, double lon, IMediator m, CancellationToken ct) =>
            m.Send(new ReverseGeocodeRequest(lat, lon), ct));
    }
}
