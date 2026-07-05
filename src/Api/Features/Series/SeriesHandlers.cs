using Api.Data;
using Api.Features.Media;
using Data.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Series;

// ── DTOs ────────────────────────────────────────────────────────────────────

public record SeriesDto(
    Guid Id, string SeriesType,
    DateTime? TakenAtStart, DateTime? TakenAtEnd,
    int MediaCount, Guid? CoverMediaId);

public record SeriesDetailDto(
    Guid Id, string SeriesType,
    DateTime? TakenAtStart, DateTime? TakenAtEnd,
    Guid? CoverMediaId,
    IReadOnlyList<MediaListItemDto> Media);

// ── Requests ────────────────────────────────────────────────────────────────

public record GetSeriesListRequest : IRequest<IReadOnlyList<SeriesDto>>;
public record GetSeriesDetailRequest(Guid Id) : IRequest<SeriesDetailDto?>;
public record UpdateSeriesRequest(Guid Id, Guid? CoverMediaId, string? SeriesType) : IRequest<bool>;
public record AddSeriesMediaRequest(Guid SeriesId, Guid MediaFileId) : IRequest<bool>;
public record RemoveSeriesMediaRequest(Guid SeriesId, Guid MediaFileId) : IRequest<bool>;

// ── Handlers ────────────────────────────────────────────────────────────────

public sealed class GetSeriesListHandler : IRequestHandler<GetSeriesListRequest, IReadOnlyList<SeriesDto>>
{
    private readonly AppDbContext _db;
    public GetSeriesListHandler(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<SeriesDto>> Handle(GetSeriesListRequest req, CancellationToken ct)
    {
        return await _db.PhotoSeries
            .Select(s => new SeriesDto(s.Id, s.SeriesType, s.TakenAtStart, s.TakenAtEnd,
                s.SeriesMediaItems.Count, s.CoverMediaId))
            .OrderByDescending(s => s.TakenAtStart)
            .ToListAsync(ct);
    }
}

public sealed class GetSeriesDetailHandler : IRequestHandler<GetSeriesDetailRequest, SeriesDetailDto?>
{
    private readonly AppDbContext _db;
    public GetSeriesDetailHandler(AppDbContext db) => _db = db;

    public async Task<SeriesDetailDto?> Handle(GetSeriesDetailRequest req, CancellationToken ct)
    {
        var s = await _db.PhotoSeries.Where(x => x.Id == req.Id).FirstOrDefaultAsync(ct);
        if (s is null) return null;

        var media = await _db.SeriesMedia
            .Include(sm => sm.MediaFile!.Thumbnail)
            .Include(sm => sm.MediaFile!.Metadata)
            .Where(sm => sm.SeriesId == req.Id && !sm.MediaFile!.IsDeleted)
            .Select(sm => sm.MediaFile!)
            .ToListAsync(ct);

        return new SeriesDetailDto(s.Id, s.SeriesType, s.TakenAtStart, s.TakenAtEnd,
            s.CoverMediaId, media.Select(f => f.ToListItemDto()).ToList());
    }
}

public sealed class UpdateSeriesHandler : IRequestHandler<UpdateSeriesRequest, bool>
{
    private readonly AppDbContext _db;
    public UpdateSeriesHandler(AppDbContext db) => _db = db;

    public async Task<bool> Handle(UpdateSeriesRequest req, CancellationToken ct)
    {
        var s = await _db.PhotoSeries.AsTracking().Where(x => x.Id == req.Id).FirstOrDefaultAsync(ct);
        if (s is null) return false;

        if (req.CoverMediaId.HasValue) s.CoverMediaId = req.CoverMediaId.Value;
        if (req.SeriesType is not null) s.SeriesType   = req.SeriesType;

        await _db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class AddSeriesMediaHandler : IRequestHandler<AddSeriesMediaRequest, bool>
{
    private readonly AppDbContext _db;
    public AddSeriesMediaHandler(AppDbContext db) => _db = db;

    public async Task<bool> Handle(AddSeriesMediaRequest req, CancellationToken ct)
    {
        var exists = await _db.SeriesMedia
            .AnyAsync(sm => sm.SeriesId == req.SeriesId && sm.MediaFileId == req.MediaFileId, ct);

        if (exists) return true;

        _db.SeriesMedia.Add(new SeriesMedia { SeriesId = req.SeriesId, MediaFileId = req.MediaFileId });
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class RemoveSeriesMediaHandler : IRequestHandler<RemoveSeriesMediaRequest, bool>
{
    private readonly AppDbContext _db;
    public RemoveSeriesMediaHandler(AppDbContext db) => _db = db;

    public async Task<bool> Handle(RemoveSeriesMediaRequest req, CancellationToken ct)
    {
        var deleted = await _db.SeriesMedia
            .Where(sm => sm.SeriesId == req.SeriesId && sm.MediaFileId == req.MediaFileId)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }
}
