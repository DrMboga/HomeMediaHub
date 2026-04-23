using GeoService.Data;
using GeoService.Models;
using GeoService.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GeoService.Features.Geocoding;

public sealed class ReverseGeocodeHandler : IRequestHandler<ReverseGeocodeRequest, ReverseGeocodeResult>
{
    private readonly GeoDbContext    _db;
    private readonly NominatimClient _nominatim;

    public ReverseGeocodeHandler(GeoDbContext db, NominatimClient nominatim)
    {
        _db        = db;
        _nominatim = nominatim;
    }

    public async Task<ReverseGeocodeResult> Handle(ReverseGeocodeRequest request, CancellationToken cancellationToken)
    {
        var lat = Math.Round(request.Lat, 4);
        var lon = Math.Round(request.Lon, 4);

        var cached = await _db.GeoCacheEntries
            .FirstOrDefaultAsync(g => g.Lat == lat && g.Lon == lon, cancellationToken);

        if (cached is not null)
            return new ReverseGeocodeResult(cached.Address, Cached: true);

        var address = await _nominatim.ReverseGeocodeAsync(lat, lon, cancellationToken);

        if (address is not null)
        {
            _db.GeoCacheEntries.Add(new GeoCacheEntry { Lat = lat, Lon = lon, Address = address });
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new ReverseGeocodeResult(address, Cached: false);
    }
}
