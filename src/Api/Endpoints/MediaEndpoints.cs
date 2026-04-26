using Api.Features.Media.SaveMedia;
using MediatR;

namespace Api.Endpoints;

public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/media");
        g.MapPost("", async (SaveMediaRequest req, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(req, ct);
            return Results.Created($"/api/media/{result.Id}", result);
        });
    }
}
