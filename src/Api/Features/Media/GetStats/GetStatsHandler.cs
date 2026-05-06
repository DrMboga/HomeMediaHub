using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.GetStats;

public record GetStatsRequest : IRequest<StatsDto>;

public record StatsDto(int Photos, int Videos, DateTime? LatestDate, int Countries);

public sealed class GetStatsHandler : IRequestHandler<GetStatsRequest, StatsDto>
{
    private readonly AppDbContext _db;
    public GetStatsHandler(AppDbContext db) => _db = db;

    public async Task<StatsDto> Handle(GetStatsRequest req, CancellationToken ct)
    {
        var q      = _db.MediaFiles.Where(f => !f.IsDeleted);
        var total  = await q.CountAsync(ct);
        var videos = await q.CountAsync(f => f.DurationSec.HasValue, ct);
        var latest = await q.MaxAsync(f => (DateTime?)f.TakenAt, ct);

        var addresses = await _db.MediaMetadata
            .Where(m => m.Address != null)
            .Select(m => m.Address!)
            .ToListAsync(ct);

        var countries = addresses
            .Select(a => a.Split(',').Last().Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new StatsDto(total - videos, videos, latest, countries);
    }
}
