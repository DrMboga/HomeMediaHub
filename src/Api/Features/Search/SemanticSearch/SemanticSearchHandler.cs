using System.Net.Http.Json;
using Api.Data;
using Api.Features.Media;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Api.Features.Search.SemanticSearch;

public record SemanticSearchRequest(string Query, int Limit = 50, DateTime? From = null, DateTime? To = null, string? OriginClass = null) : IRequest<IReadOnlyList<MediaListItemDto>>;

public sealed class SemanticSearchHandler : IRequestHandler<SemanticSearchRequest, IReadOnlyList<MediaListItemDto>>
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;

    public SemanticSearchHandler(AppDbContext db, IHttpClientFactory http, IConfiguration config)
    {
        _db     = db;
        _http   = http;
        _config = config;
    }

    public async Task<IReadOnlyList<MediaListItemDto>> Handle(SemanticSearchRequest req, CancellationToken ct)
    {
        // Get query embedding from AI sidecar. The prompt template matches how CLIP was
        // trained (image-caption pairs) — bare keywords embed noticeably worse.
        var client   = _http.CreateClient("AiSidecar");
        var response = await client.PostAsJsonAsync("/embed/text", new { text = $"a photo of {req.Query}" }, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbedTextResponse>(ct);
        if (result?.Embedding is not { Length: 512 })
            return [];

        var queryVector = new Vector(result.Embedding);
        var maxDistance = _config.GetValue("Search:MaxCosineDistance", 0.8);

        // Find top-N media by cosine distance, dropping anything past the relevance cutoff —
        // without it, a query with few/no good matches still returns Limit unrelated photos.
        var q = _db.MediaEmbeddings
            .Join(_db.MediaFiles, e => e.MediaFileId, f => f.Id, (e, f) => new { e, f })
            .Where(x => !x.f.IsDeleted && x.f.OriginClass != "meme");

        if (req.From.HasValue)
            q = q.Where(x => x.f.TakenAt >= DateTime.SpecifyKind(req.From.Value, DateTimeKind.Utc));
        if (req.To.HasValue)
            q = q.Where(x => x.f.TakenAt < DateTime.SpecifyKind(req.To.Value.AddDays(1), DateTimeKind.Utc));
        if (!string.IsNullOrEmpty(req.OriginClass))
            q = q.Where(x => x.f.OriginClass == req.OriginClass);

        var ranked = await q
            .Select(x => new { x.f.Id, Distance = x.e.Embedding!.CosineDistance(queryVector) })
            .Where(x => x.Distance <= maxDistance)
            .OrderBy(x => x.Distance)
            .Take(req.Limit)
            .ToListAsync(ct);

        if (ranked.Count == 0) return [];

        var ids   = ranked.Select(r => r.Id).ToList();
        var files = await _db.MediaFiles
            .Include(f => f.Thumbnail)
            .Include(f => f.Metadata)
            .Where(f => ids.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        // Preserve relevance order (ascending distance) — the frontend renders API order as-is.
        return ranked
            .Select(r => files[r.Id].ToListItemDto())
            .ToList();
    }

    private sealed record EmbedTextResponse(float[] Embedding);
}
