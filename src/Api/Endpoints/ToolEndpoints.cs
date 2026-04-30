using Api.Features.Tools.Convert;
using Api.Features.Tools.Dedup;
using Api.Features.Tools.Hash;
using Api.Features.Tools.Metadata;
using Api.Features.Tools.RecordFailure;
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

        g.MapGet("/dedup", async (string hash, IMediator m, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(hash))
                return Results.BadRequest(new { error = "hash is required" });
            return Results.Ok(await m.Send(new DedupRequest(hash), ct));
        });

        g.MapGet("/new-id", () => Results.Ok(new { id = Guid.NewGuid() }));

        g.MapPost("/convert", (ConvertFileRequest req, IMediator m, CancellationToken ct) => m.Send(req, ct));

        g.MapPost("/record-failure", (RecordFailureRequest req, IMediator m, CancellationToken ct) => m.Send(req, ct));

        g.MapPost("/cleanup", (CleanupRequest req) =>
        {
            if (!req.FilePath.StartsWith("/shared-temp/"))
                return Results.Ok(new { deleted = false });
            if (File.Exists(req.FilePath))
                File.Delete(req.FilePath);
            return Results.Ok(new { deleted = true });
        });
    }
}

public sealed record CleanupRequest(string FilePath);
