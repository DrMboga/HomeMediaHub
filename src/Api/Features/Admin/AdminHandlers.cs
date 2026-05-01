using System.Net.Http.Json;
using Api.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Api.Features.Admin;

// ── DTOs ────────────────────────────────────────────────────────────────────

public record JobDto(
    Guid Id, string FilePath, string Source, string Status,
    string? N8nExecutionId, DateTime StartedAt, DateTime? CompletedAt,
    string? FailedStep, string? ErrorMessage, int RetryCount);

// ── Requests ────────────────────────────────────────────────────────────────

public record GetJobsRequest(string? Status, int Page = 1, int PageSize = 50)
    : IRequest<IReadOnlyList<JobDto>>;

public record GetJobDetailRequest(Guid Id) : IRequest<JobDto?>;

public record RetryJobRequest(Guid Id) : IRequest<bool>;

// ── Handlers ────────────────────────────────────────────────────────────────

public sealed class GetJobsHandler : IRequestHandler<GetJobsRequest, IReadOnlyList<JobDto>>
{
    private readonly AppDbContext _db;
    public GetJobsHandler(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<JobDto>> Handle(GetJobsRequest req, CancellationToken ct)
    {
        var q = _db.ProcessingJobs.AsQueryable();
        if (!string.IsNullOrEmpty(req.Status))
            q = q.Where(j => j.Status == req.Status);

        return await q
            .OrderByDescending(j => j.StartedAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(j => new JobDto(j.Id, j.FilePath, j.Source, j.Status,
                j.N8nExecutionId, j.StartedAt, j.CompletedAt,
                j.FailedStep, j.ErrorMessage, j.RetryCount))
            .ToListAsync(ct);
    }
}

public sealed class GetJobDetailHandler : IRequestHandler<GetJobDetailRequest, JobDto?>
{
    private readonly AppDbContext _db;
    public GetJobDetailHandler(AppDbContext db) => _db = db;

    public async Task<JobDto?> Handle(GetJobDetailRequest req, CancellationToken ct)
    {
        var j = await _db.ProcessingJobs.Where(x => x.Id == req.Id).FirstOrDefaultAsync(ct);
        if (j is null) return null;

        return new JobDto(j.Id, j.FilePath, j.Source, j.Status,
            j.N8nExecutionId, j.StartedAt, j.CompletedAt,
            j.FailedStep, j.ErrorMessage, j.RetryCount);
    }
}

public sealed class RetryJobHandler : IRequestHandler<RetryJobRequest, bool>
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;

    public RetryJobHandler(AppDbContext db, IHttpClientFactory http, IConfiguration config)
    {
        _db     = db;
        _http   = http;
        _config = config;
    }

    public async Task<bool> Handle(RetryJobRequest req, CancellationToken ct)
    {
        var job = await _db.ProcessingJobs
            .Where(j => j.Id == req.Id && j.Status == "failed")
            .FirstOrDefaultAsync(ct);

        if (job is null) return false;

        var webhookUrl = _config["N8nWebhookUrl"] ?? "http://n8n:5678/webhook/process-file";
        var client     = _http.CreateClient();
        var payload    = new { filePath = job.FilePath, source = job.Source };

        try
        {
            var resp = await client.PostAsJsonAsync(webhookUrl, payload, ct);
            resp.EnsureSuccessStatusCode();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
