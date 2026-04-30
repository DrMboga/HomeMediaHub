using MediatR;

namespace Api.Features.Tools.RecordFailure;

public sealed record RecordFailureRequest(
    string  FilePath,
    string  Source,
    string? N8nExecutionId,
    string? FailedStep,
    string? ErrorMessage
) : IRequest<RecordFailureResponse>;

public sealed record RecordFailureResponse(Guid Id);
