using System.Net.Http.Json;
using Api.Data;
using Api.Features.Media;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Api.Features.Search.SemanticSearch;

public record SemanticSearchRequest(string Query, int Limit = 50, DateTime? From = null, DateTime? To = null) : IRequest<IReadOnlyList<MediaListItemDto>>;

public sealed class SemanticSearchHandler : IRequestHandler<SemanticSearchRequest, IReadOnlyList<MediaListItemDto>>
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;

    public SemanticSearchHandler(AppDbContext db, IHttpClientFactory http)
    {
        _db   = db;
        _http = http;
    }

    public async Task<IReadOnlyList<MediaListItemDto>> Handle(SemanticSearchRequest req, CancellationToken ct)
    {
        // Get query embedding from AI sidecar
        var client   = _http.CreateClient("AiSidecar");
        var response = await client.PostAsJsonAsync("/embed/text", new { text = req.Query }, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbedTextResponse>(ct);
        if (result?.Embedding is not { Length: 512 })
            return [];

        var queryVector = new Vector(result.Embedding);

        // Find top-N media by cosine distance
        var q = _db.MediaEmbeddings
            .Join(_db.MediaFiles, e => e.MediaFileId, f => f.Id, (e, f) => new { e, f })
            .Where(x => !x.f.IsDeleted);

        if (req.From.HasValue)
            q = q.Where(x => x.f.TakenAt >= DateTime.SpecifyKind(req.From.Value, DateTimeKind.Utc));
        if (req.To.HasValue)
            q = q.Where(x => x.f.TakenAt < DateTime.SpecifyKind(req.To.Value.AddDays(1), DateTimeKind.Utc));

        var ids = await q
            .OrderBy(x => x.e.Embedding!.CosineDistance(queryVector))
            .Take(req.Limit)
            .Select(x => x.f.Id)
            .ToListAsync(ct);

        if (ids.Count == 0) return [];

        var files = await _db.MediaFiles
            .Include(f => f.Thumbnail)
            .Include(f => f.Metadata)
            .Where(f => ids.Contains(f.Id))
            .ToListAsync(ct);

        // Preserve distance order
        return ids
            .Select(id => files.FirstOrDefault(f => f.Id == id))
            .Where(f => f is not null)
            .Select(f => f!.ToListItemDto())
            .ToList();
    }

    private sealed record EmbedTextResponse(float[] Embedding);
}
