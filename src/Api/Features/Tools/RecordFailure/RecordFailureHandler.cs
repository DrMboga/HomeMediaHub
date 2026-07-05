using Api.Data;
using Data.Entities;
using MediatR;

namespace Api.Features.Tools.RecordFailure;

public sealed class RecordFailureHandler : IRequestHandler<RecordFailureRequest, RecordFailureResponse>
{
    private readonly AppDbContext _db;

    public RecordFailureHandler(AppDbContext db) => _db = db;

    public async Task<RecordFailureResponse> Handle(RecordFailureRequest req, CancellationToken ct)
    {
        var job = new ProcessingJob
        {
            Id             = Guid.NewGuid(),
            FilePath       = req.FilePath,
            Source         = req.Source,
            Status         = "failed",
            FailedStep     = req.FailedStep,
            ErrorMessage   = req.ErrorMessage,
        };

        _db.ProcessingJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        return new RecordFailureResponse(job.Id);
    }
}
