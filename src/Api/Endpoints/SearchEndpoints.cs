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

        g.MapGet("", async (
            [FromQuery] string q,
            [FromQuery] int limit,
            IMediator m, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "q is required" });

            var items = await m.Send(new SemanticSearchRequest(q, limit > 0 ? limit : 50), ct);
            return Results.Ok(items);
        });

        g.MapGet("/tags", async (
            [FromQuery] string tag,
            [FromQuery] int limit,
            IMediator m, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(tag))
                return Results.BadRequest(new { error = "tag is required" });

            var items = await m.Send(new TagSearchRequest(tag, limit > 0 ? limit : 100), ct);
            return Results.Ok(items);
        });
    }
}
