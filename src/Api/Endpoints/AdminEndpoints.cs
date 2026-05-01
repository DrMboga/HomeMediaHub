using Api.Features.Admin;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin/jobs").RequireAuthorization();

        g.MapGet("", async (
            [FromQuery] string? status,
            [FromQuery] int page,
            [FromQuery] int pageSize,
            IMediator m, CancellationToken ct) =>
        {
            var jobs = await m.Send(new GetJobsRequest(
                status,
                page > 0 ? page : 1,
                pageSize is > 0 and <= 200 ? pageSize : 50), ct);
            return Results.Ok(jobs);
        });

        g.MapGet("/{id:guid}", async (Guid id, IMediator m, CancellationToken ct) =>
        {
            var job = await m.Send(new GetJobDetailRequest(id), ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        g.MapPost("/{id:guid}/retry", async (Guid id, IMediator m, CancellationToken ct) =>
        {
            var ok = await m.Send(new RetryJobRequest(id), ct);
            return ok ? Results.Accepted() : Results.NotFound();
        });
    }
}
