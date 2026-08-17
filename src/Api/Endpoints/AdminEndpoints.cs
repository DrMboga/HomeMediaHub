using Api.Features.Admin;
using Api.Features.PhotoOfTheDay;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization();

        admin.MapPost("/scan", async ([FromBody] TriggerScanBody? body, IMediator m, CancellationToken ct) =>
        {
            var scanId = await m.Send(new TriggerScanRequest(body?.Path), ct);
            return Results.Accepted(value: new { scanId });
        });

        admin.MapGet("/pipeline/status", async (IMediator m, CancellationToken ct) =>
            Results.Ok(await m.Send(new GetPipelineStatusRequest(), ct)));

        var jobs = admin.MapGroup("/jobs");

        jobs.MapGet("", async (
            [FromQuery] string? status,
            [FromQuery] int page,
            [FromQuery] int pageSize,
            IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(new GetJobsRequest(
                status,
                page > 0 ? page : 1,
                pageSize is > 0 and <= 200 ? pageSize : 50), ct);
            return Results.Ok(result);
        });

        jobs.MapGet("/{id:guid}", async (Guid id, IMediator m, CancellationToken ct) =>
        {
            var job = await m.Send(new GetJobDetailRequest(id), ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        jobs.MapPost("/{id:guid}/retry", async (Guid id, IMediator m, CancellationToken ct) =>
        {
            var ok = await m.Send(new RetryJobRequest(id), ct);
            return ok ? Results.Accepted() : Results.NotFound();
        });

        jobs.MapPost("/retry-failed", async (IMediator m, CancellationToken ct) =>
        {
            var count = await m.Send(new RetryFailedJobsRequest(), ct);
            return Results.Ok(new { retried = count });
        });

        var potd = admin.MapGroup("/potd");

        // Runs the pick-and-send once, now. Three deliberate differences from the scheduled path
        // (architecture.md §14): it ignores Potd:Enabled — that flag stops the *scheduler* posting
        // from stage, and a button press is not an accident; it ignores the already-sent-today
        // guard, so it can be run repeatedly while debugging; and it does not write potd_sends, so
        // a test at 11:00 cannot make the real 15:00 run skip the day.
        potd.MapPost("/send", async ([FromBody] SendPotdBody? body, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(new SendPhotoOfTheDayRequest(
                body?.Month, body?.Day, body?.MediaFileId, body?.DryRun ?? false), ct);
            return Results.Ok(result);
        });

        potd.MapGet("/last", async ([FromQuery] int limit, IMediator m, CancellationToken ct) =>
            Results.Ok(await m.Send(new GetLastPotdSendsRequest(limit is > 0 and <= 100 ? limit : 10), ct)));
    }
}

public record TriggerScanBody(string? Path);

public record SendPotdBody(int? Month, int? Day, Guid? MediaFileId, bool? DryRun);
