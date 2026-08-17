using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.PhotoOfTheDay;

public record PotdSendDto(DateOnly SentOn, Guid MediaFileId, DateTime SentAt);

public record GetLastPotdSendsRequest(int Limit = 10) : IRequest<IReadOnlyList<PotdSendDto>>;

/// <summary>Backs the Admin page's "last sent" line. Only the scheduler writes these rows.</summary>
public sealed class GetLastPotdSendsHandler
    : IRequestHandler<GetLastPotdSendsRequest, IReadOnlyList<PotdSendDto>>
{
    private readonly AppDbContext _db;
    public GetLastPotdSendsHandler(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<PotdSendDto>> Handle(GetLastPotdSendsRequest req, CancellationToken ct) =>
        await _db.PotdSends
            .OrderByDescending(s => s.SentOn)
            .Take(req.Limit)
            .Select(s => new PotdSendDto(s.SentOn, s.MediaFileId, s.SentAt))
            .ToListAsync(ct);
}
