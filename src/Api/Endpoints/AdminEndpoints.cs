using Api.Features.Admin;
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
    }
}

public record TriggerScanBody(string? Path);
