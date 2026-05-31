using Api.Features.Search.SemanticSearch;
using Api.Features.Search.TagSearch;
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

            var items = await m.Send(new SemanticSearchRequest(q, limit > 0 ? limit : 50, from, to, originClass), ct);
            return Results.Ok(items);
        });

        g.MapGet("/tags", async (
            IMediator m,
            CancellationToken ct,
            [FromQuery] string q = "",
            [FromQuery] int limit = 0) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "q is required" });

            var items = await m.Send(new TagSearchRequest(q, limit > 0 ? limit : 100), ct);
            return Results.Ok(items);
        });
    }
}
