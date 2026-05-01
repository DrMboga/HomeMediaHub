using Api.Data;
using Api.Features.Media;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Media.GetMediaDetail;

public record GetMediaDetailRequest(Guid Id) : IRequest<MediaDetailDto?>;

public sealed class GetMediaDetailHandler : IRequestHandler<GetMediaDetailRequest, MediaDetailDto?>
{
    private readonly AppDbContext _db;

    public GetMediaDetailHandler(AppDbContext db) => _db = db;

    public async Task<MediaDetailDto?> Handle(GetMediaDetailRequest req, CancellationToken ct)
    {
        var f = await _db.MediaFiles
            .Include(x => x.Metadata)
            .Include(x => x.Tags)
            .Where(x => x.Id == req.Id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (f is null) return null;

        return new MediaDetailDto(
            f.Id, f.FilePath, f.MimeType, f.FileSize, f.Width, f.Height,
            f.DurationSec, f.TakenAt, f.IsFavorite, f.OriginClass, f.OriginConfidence,
            f.Metadata?.CameraMake, f.Metadata?.CameraModel,
            f.Metadata?.Latitude, f.Metadata?.Longitude, f.Metadata?.Address,
            f.Tags.Select(t => new TagDto(t.Tag, t.Confidence, t.Model)).ToList());
    }
}
