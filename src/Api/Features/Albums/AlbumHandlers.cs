using Api.Data;
using Api.Features.Media;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Albums;

// ── DTOs ────────────────────────────────────────────────────────────────────

public record AlbumDto(Guid Id, string Name, string? Description, int MediaCount, DateTime CreatedAt);

public record AlbumDetailDto(Guid Id, string Name, string? Description,
    IReadOnlyList<MediaListItemDto> Media, DateTime CreatedAt);

// ── Requests ────────────────────────────────────────────────────────────────

public record GetAlbumsRequest : IRequest<IReadOnlyList<AlbumDto>>;

public record CreateAlbumRequest(string Name, string? Description) : IRequest<AlbumDto>;

public record GetAlbumDetailRequest(Guid Id) : IRequest<AlbumDetailDto?>;

public record UpdateAlbumRequest(Guid Id, string? Name, string? Description) : IRequest<bool>;

public record DeleteAlbumRequest(Guid Id) : IRequest<bool>;

public record AddAlbumMediaRequest(Guid AlbumId, Guid MediaFileId) : IRequest<bool>;

public record RemoveAlbumMediaRequest(Guid AlbumId, Guid MediaFileId) : IRequest<bool>;

// ── Handlers ────────────────────────────────────────────────────────────────

public sealed class GetAlbumsHandler : IRequestHandler<GetAlbumsRequest, IReadOnlyList<AlbumDto>>
{
    private readonly AppDbContext _db;
    public GetAlbumsHandler(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<AlbumDto>> Handle(GetAlbumsRequest req, CancellationToken ct)
    {
        return await _db.Albums
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new AlbumDto(a.Id, a.Name, a.Description,
                a.AlbumMediaItems.Count, a.CreatedAt))
            .ToListAsync(ct);
    }
}

public sealed class CreateAlbumHandler : IRequestHandler<CreateAlbumRequest, AlbumDto>
{
    private readonly AppDbContext _db;
    public CreateAlbumHandler(AppDbContext db) => _db = db;

    public async Task<AlbumDto> Handle(CreateAlbumRequest req, CancellationToken ct)
    {
        var album = new Album { Id = Guid.NewGuid(), Name = req.Name, Description = req.Description };
        _db.Albums.Add(album);
        await _db.SaveChangesAsync(ct);
        return new AlbumDto(album.Id, album.Name, album.Description, 0, album.CreatedAt);
    }
}

public sealed class GetAlbumDetailHandler : IRequestHandler<GetAlbumDetailRequest, AlbumDetailDto?>
{
    private readonly AppDbContext _db;
    public GetAlbumDetailHandler(AppDbContext db) => _db = db;

    public async Task<AlbumDetailDto?> Handle(GetAlbumDetailRequest req, CancellationToken ct)
    {
        var album = await _db.Albums
            .Where(a => a.Id == req.Id)
            .FirstOrDefaultAsync(ct);

        if (album is null) return null;

        var media = await _db.AlbumMedia
            .Include(am => am.MediaFile!.Thumbnail)
            .Include(am => am.MediaFile!.Metadata)
            .Where(am => am.AlbumId == req.Id && !am.MediaFile!.IsDeleted)
            .OrderBy(am => am.AddedAt)
            .Select(am => am.MediaFile!)
            .ToListAsync(ct);

        return new AlbumDetailDto(album.Id, album.Name, album.Description,
            media.Select(f => f.ToListItemDto()).ToList(), album.CreatedAt);
    }
}

public sealed class UpdateAlbumHandler : IRequestHandler<UpdateAlbumRequest, bool>
{
    private readonly AppDbContext _db;
    public UpdateAlbumHandler(AppDbContext db) => _db = db;

    public async Task<bool> Handle(UpdateAlbumRequest req, CancellationToken ct)
    {
        var album = await _db.Albums.AsTracking().Where(a => a.Id == req.Id).FirstOrDefaultAsync(ct);
        if (album is null) return false;

        if (req.Name is not null)        album.Name        = req.Name;
        if (req.Description is not null) album.Description = req.Description;

        await _db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class DeleteAlbumHandler : IRequestHandler<DeleteAlbumRequest, bool>
{
    private readonly AppDbContext _db;
    public DeleteAlbumHandler(AppDbContext db) => _db = db;

    public async Task<bool> Handle(DeleteAlbumRequest req, CancellationToken ct)
    {
        var deleted = await _db.Albums.Where(a => a.Id == req.Id).ExecuteDeleteAsync(ct);
        return deleted > 0;
    }
}

public sealed class AddAlbumMediaHandler : IRequestHandler<AddAlbumMediaRequest, bool>
{
    private readonly AppDbContext _db;
    public AddAlbumMediaHandler(AppDbContext db) => _db = db;

    public async Task<bool> Handle(AddAlbumMediaRequest req, CancellationToken ct)
    {
        var exists = await _db.AlbumMedia
            .AnyAsync(am => am.AlbumId == req.AlbumId && am.MediaFileId == req.MediaFileId, ct);

        if (exists) return true;

        _db.AlbumMedia.Add(new AlbumMedia { AlbumId = req.AlbumId, MediaFileId = req.MediaFileId });
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class RemoveAlbumMediaHandler : IRequestHandler<RemoveAlbumMediaRequest, bool>
{
    private readonly AppDbContext _db;
    public RemoveAlbumMediaHandler(AppDbContext db) => _db = db;

    public async Task<bool> Handle(RemoveAlbumMediaRequest req, CancellationToken ct)
    {
        var deleted = await _db.AlbumMedia
            .Where(am => am.AlbumId == req.AlbumId && am.MediaFileId == req.MediaFileId)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }
}
