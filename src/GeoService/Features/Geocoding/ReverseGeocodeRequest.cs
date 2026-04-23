using MediatR;

namespace GeoService.Features.Geocoding;

public record ReverseGeocodeRequest(double Lat, double Lon) : IRequest<ReverseGeocodeResult>;
public record ReverseGeocodeResult(string? Address, bool Cached);
