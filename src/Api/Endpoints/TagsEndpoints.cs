using Api.Features.Tags;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Api.Endpoints;

public static class TagsEndpoints
{
    public static void MapTagsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/tags").RequireAuthorization();

        // One endpoint serves both the initial chip row (no `selected`) and the facet
        // narrowing that follows each toggle (`selected` repeated per active chip).
        g.MapGet("", async ([FromQuery(Name = "selected")] string[]? selected, IMediator m, CancellationToken ct) =>
            Results.Ok(await m.Send(new GetTagsRequest(Normalize(selected)), ct)));

        g.MapGet("/media", async (
            [FromQuery(Name = "tags")] string[]? tags,
            [FromQuery] int page,
            [FromQuery] int pageSize,
            IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(new GetTagMediaRequest(
                Normalize(tags),
                page > 0 ? page : 1,
                pageSize is > 0 and <= 200 ? pageSize : 50), ct);
            return Results.Ok(result);
        });
    }

    // Repeated query params arrive as-typed: trim, drop blanks, and dedupe so a
    // doubled chip cannot add a redundant EXISTS to the AND chain.
    private static string[] Normalize(string[]? values) =>
        values is null
            ? []
            : values.Select(v => v.Trim())
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
}
