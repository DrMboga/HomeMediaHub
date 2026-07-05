namespace Contracts.Messages;

/// <summary>Published by .NET consumers on an ingest-stage failure; consumed by
/// JobFailureConsumer, which marks the processing_jobs row failed.</summary>
public record MediaProcessingFailed(
    Guid? JobId,
    Guid? MediaFileId,
    string FilePath,
    string Stage,
    string Error,
    DateTimeOffset OccurredAt);
