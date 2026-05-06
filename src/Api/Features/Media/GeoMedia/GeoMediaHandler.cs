using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.GeoMedia;

public record GeoMediaRequest : IRequest<List<GeoPinDto>>;

public record GeoPinDto(Guid Id, double Lat, double Lng, string? Address);

public sealed class GeoMediaHandler : IRequestHandler<GeoMediaRequest, List<GeoPinDto>>
{
    private readonly AppDbContext _db;
    public GeoMediaHandler(AppDbContext db) => _db = db;

    public async Task<List<GeoPinDto>> Handle(GeoMediaRequest req, CancellationToken ct)
    {
        return await (
            from f in _db.MediaFiles
            join m in _db.MediaMetadata on f.Id equals m.MediaFileId
            where !f.IsDeleted && m.Latitude.HasValue && m.Longitude.HasValue
            select new GeoPinDto(f.Id, m.Latitude!.Value, m.Longitude!.Value, m.Address)
        ).ToListAsync(ct);
    }
}
