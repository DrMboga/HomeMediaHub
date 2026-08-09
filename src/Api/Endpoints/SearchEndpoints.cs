using Api.Features.Search.SemanticSearch;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Api.Endpoints;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/search").RequireAuthorization();

        g.MapGet("/semantic", async (
            IMediator m,
            CancellationToken ct,
            [FromQuery] string q = "",
            [FromQuery] int limit = 0,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] string? originClass = null) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "q is required" });

            // 15 by default: the old 50 filled the page with near-noise on narrow queries
            // (only ~2 real matches for "bridge", but 50 rows still cleared the cutoff).
            var items = await m.Send(new SemanticSearchRequest(q, limit > 0 ? limit : 15, from, to, originClass), ct);
            return Results.Ok(items);
        });

        // GET /api/search/tags retired in Phase 10 — browse-by-tag lives at /api/tags,
        // which pages its results and does facet narrowing. Two tag routes would compete.
    }
}
