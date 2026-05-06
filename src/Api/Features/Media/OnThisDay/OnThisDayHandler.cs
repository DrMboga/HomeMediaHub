using Api.Data;
using Api.Features.Media;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.OnThisDay;

public record OnThisDayRequest(int Month, int Day) : IRequest<List<OtdGroupDto>>;

public record OtdGroupDto(int Year, List<MediaListItemDto> Items);

public sealed class OnThisDayHandler : IRequestHandler<OnThisDayRequest, List<OtdGroupDto>>
{
    private readonly AppDbContext _db;
    public OnThisDayHandler(AppDbContext db) => _db = db;

    public async Task<List<OtdGroupDto>> Handle(OnThisDayRequest req, CancellationToken ct)
    {
        var items = await _db.MediaFiles
            .Include(f => f.Thumbnail)
            .Include(f => f.Metadata)
            .Where(f => !f.IsDeleted &&
                        f.TakenAt.HasValue &&
                        f.TakenAt.Value.Month == req.Month &&
                        f.TakenAt.Value.Day == req.Day)
            .OrderByDescending(f => f.TakenAt)
            .Take(200)
            .ToListAsync(ct);

        return items
            .GroupBy(f => f.TakenAt!.Value.Year)
            .OrderByDescending(g => g.Key)
            .Select(g => new OtdGroupDto(g.Key, g.Select(f => f.ToListItemDto()).ToList()))
            .ToList();
    }
}
