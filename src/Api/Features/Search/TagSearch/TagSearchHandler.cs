using Api.Data;
using Api.Features.Media;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Search.TagSearch;

public record TagSearchRequest(string Tag, int Limit = 100) : IRequest<IReadOnlyList<MediaListItemDto>>;

public sealed class TagSearchHandler : IRequestHandler<TagSearchRequest, IReadOnlyList<MediaListItemDto>>
{
    private readonly AppDbContext _db;

    public TagSearchHandler(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<MediaListItemDto>> Handle(TagSearchRequest req, CancellationToken ct)
    {
        var items = await _db.MediaFiles
            .Include(f => f.Thumbnail)
            .Include(f => f.Metadata)
            .Where(f => !f.IsDeleted && f.Tags.Any(t => t.Tag == req.Tag))
            .OrderByDescending(f => f.TakenAt)
            .Take(req.Limit)
            .ToListAsync(ct);

        return items.Select(f => f.ToListItemDto()).ToList();
    }
}
