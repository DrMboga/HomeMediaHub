using Api.Data;
using Api.Features.Media;
using Api.Features.Media.BulkDeleteMedia;
using Api.Features.Media.DeleteMedia;
using Api.Features.Media.GetMediaDetail;
using Api.Features.Media.GetMediaList;
using Api.Features.Media.GetReviewQueue;
using Api.Features.Media.SaveMedia;
using Api.Features.Media.StreamMedia;
using Api.Features.Media.UpdateMedia;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Api.Endpoints;

public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this WebApplication app)
    {
        // ── n8n tool endpoint (no auth) ──────────────────────────────────
        app.MapPost("/api/media", async (SaveMediaRequest req, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(req, ct);
            return Results.Created($"/api/media/{result.Id}", result);
        });

        // ── Frontend endpoints (auth required) ───────────────────────────
        var g = app.MapGroup("/api/media").RequireAuthorization();

        // Review queue — must come before /{id} to avoid route conflict
        g.MapGet("/review", async (
            [FromQuery] int limit,
            IMediator m, CancellationToken ct) =>
        {
            var items = await m.Send(new GetReviewQueueRequest(limit > 0 ? limit : 100), ct);
            return Results.Ok(items);
        });

        // Bulk delete — must come before /{id}
        g.MapDelete("/bulk", async (
            [FromBody] BulkDeleteRequest req,
            IMediator m, CancellationToken ct) =>
        {
            var count = await m.Send(new BulkDeleteMediaRequest(req.Ids), ct);
            return Results.Ok(new { deleted = count });
        });

        g.MapGet("", async (
            [FromQuery] int page,
            [FromQuery] int pageSize,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] string? tag,
            [FromQuery] bool? favorite,
            IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(new GetMediaListRequest(
                page > 0 ? page : 1,
                pageSize is > 0 and <= 200 ? pageSize : 50,
                from, to, tag, favorite), ct);
            return Results.Ok(result);
        });

        g.MapGet("/{id:guid}", async (Guid id, IMediator m, CancellationToken ct) =>
        {
            var detail = await m.Send(new GetMediaDetailRequest(id), ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        g.MapGet("/{id:guid}/thumbnail", async (Guid id, AppDbContext db, HttpResponse response, CancellationToken ct) =>
        {
            var thumb = await db.MediaThumbnails
                .Where(t => t.MediaFileId == id)
                .Select(t => t.ThumbnailPath)
                .FirstOrDefaultAsync(ct);

            if (thumb is null) return Results.NotFound();

            response.Headers.Append("X-Accel-Redirect", $"/thumbs/{Path.GetFileName(thumb)}");
            return Results.Ok();
        });

        g.MapGet("/{id:guid}/original", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var f = await db.MediaFiles
                .Where(x => x.Id == id && !x.IsDeleted)
                .Select(x => new { x.FilePath, x.MimeType })
                .FirstOrDefaultAsync(ct);

            if (f is null || !File.Exists(f.FilePath))
                return Results.NotFound();

            return Results.File(f.FilePath, f.MimeType, enableRangeProcessing: true);
        });

        g.MapGet("/{id:guid}/stream", async (
            Guid id,
            IMediator m,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var videoCacheDir = config["VideoCacheDir"] ?? "/video-cache";
            var result = await m.Send(new StreamMediaRequest(id, videoCacheDir), ct);

            if (result is null) return Results.NotFound();

            return Results.File(result.FilePath, result.ContentType, enableRangeProcessing: true);
        });

        g.MapPatch("/{id:guid}", async (Guid id, [FromBody] PatchMediaRequest req, IMediator m, CancellationToken ct) =>
        {
            var ok = await m.Send(new UpdateMediaRequest(id, req.IsFavorite, req.SeriesId), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        g.MapDelete("/{id:guid}", async (Guid id, IMediator m, CancellationToken ct) =>
        {
            var ok = await m.Send(new SoftDeleteMediaRequest(id), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });
    }
}

public record BulkDeleteRequest(List<Guid> Ids);
public record PatchMediaRequest(bool? IsFavorite, Guid? SeriesId);
