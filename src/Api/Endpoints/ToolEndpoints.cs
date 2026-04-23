using Api.Features.Tools.Hash;
using Api.Features.Tools.Metadata;
using Api.Features.Tools.Scan;
using Api.Features.Tools.Thumbnail;
using MediatR;

namespace Api.Endpoints;

public static class ToolEndpoints
{
    public static void MapToolEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/tools");
        g.MapPost("/hash",      (HashFileRequest req, IMediator m, CancellationToken ct) => m.Send(req, ct));
        g.MapPost("/metadata",  (ExtractMetadataRequest req, IMediator m, CancellationToken ct) => m.Send(req, ct));
        g.MapPost("/thumbnail", (GenerateThumbnailRequest req, IMediator m, CancellationToken ct) => m.Send(req, ct));
        g.MapGet("/scan", async (string path, IMediator m, CancellationToken ct) =>
        {
            if (!Directory.Exists(path))
                return Results.NotFound(new { error = $"Directory not found: {path}" });
            var result = await m.Send(new ScanDirectoryRequest(path), ct);
            return Results.Ok(result);
        });
    }
}
