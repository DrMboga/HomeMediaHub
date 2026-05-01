using Api.Features.Series;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Api.Endpoints;

public static class SeriesEndpoints
{
    public static void MapSeriesEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/series").RequireAuthorization();

        g.MapGet("", async (IMediator m, CancellationToken ct) =>
            Results.Ok(await m.Send(new GetSeriesListRequest(), ct)));

        g.MapGet("/{id:guid}", async (Guid id, IMediator m, CancellationToken ct) =>
        {
            var detail = await m.Send(new GetSeriesDetailRequest(id), ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        g.MapPatch("/{id:guid}", async (Guid id, [FromBody] UpdateSeriesBody body, IMediator m, CancellationToken ct) =>
        {
            var ok = await m.Send(new UpdateSeriesRequest(id, body.CoverMediaId, body.SeriesType), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        g.MapPost("/{id:guid}/media", async (
            Guid id,
            [FromBody] AddSeriesMediaBody body,
            IMediator m, CancellationToken ct) =>
        {
            await m.Send(new AddSeriesMediaRequest(id, body.MediaFileId), ct);
            return Results.NoContent();
        });

        g.MapDelete("/{id:guid}/media/{mediaId:guid}", async (
            Guid id, Guid mediaId,
            IMediator m, CancellationToken ct) =>
        {
            var ok = await m.Send(new RemoveSeriesMediaRequest(id, mediaId), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });
    }
}

public record UpdateSeriesBody(Guid? CoverMediaId, string? SeriesType);
public record AddSeriesMediaBody(Guid MediaFileId);
