using Api.Features.Albums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Api.Endpoints;

public static class AlbumsEndpoints
{
    public static void MapAlbumsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/albums").RequireAuthorization();

        g.MapGet("", async (IMediator m, CancellationToken ct) =>
            Results.Ok(await m.Send(new GetAlbumsRequest(), ct)));

        g.MapPost("", async ([FromBody] CreateAlbumRequest req, IMediator m, CancellationToken ct) =>
        {
            var album = await m.Send(req, ct);
            return Results.Created($"/api/albums/{album.Id}", album);
        });

        g.MapGet("/{id:guid}", async (Guid id, IMediator m, CancellationToken ct) =>
        {
            var detail = await m.Send(new GetAlbumDetailRequest(id), ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        g.MapPatch("/{id:guid}", async (Guid id, [FromBody] UpdateAlbumBody body, IMediator m, CancellationToken ct) =>
        {
            var ok = await m.Send(new UpdateAlbumRequest(id, body.Name, body.Description), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        g.MapDelete("/{id:guid}", async (Guid id, IMediator m, CancellationToken ct) =>
        {
            var ok = await m.Send(new DeleteAlbumRequest(id), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        g.MapPost("/{id:guid}/media", async (
            Guid id,
            [FromBody] AddAlbumMediaBody body,
            IMediator m, CancellationToken ct) =>
        {
            await m.Send(new AddAlbumMediaRequest(id, body.MediaFileId), ct);
            return Results.NoContent();
        });

        g.MapDelete("/{id:guid}/media/{mediaId:guid}", async (
            Guid id, Guid mediaId,
            IMediator m, CancellationToken ct) =>
        {
            var ok = await m.Send(new RemoveAlbumMediaRequest(id, mediaId), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });
    }
}

public record UpdateAlbumBody(string? Name, string? Description);
public record AddAlbumMediaBody(Guid MediaFileId);
