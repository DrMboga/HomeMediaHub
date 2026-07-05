using Contracts.Messages;
using MassTransit;
using MediaWorker.Services;

namespace MediaWorker.Consumers;

/// <summary>
/// The failure sink (architecture §2/§7). Marks a <c>processing_jobs</c> row <c>failed</c> from
/// two sources: an explicit <see cref="MediaProcessingFailed"/> published by ingest, and
/// <c>Fault&lt;FileDiscovered&gt;</c> (a message whose retries were exhausted). MassTransit
/// <c>_error</c> queues remain a human-inspectable backstop; this consumer is the data path.
/// </summary>
public sealed class JobFailureConsumer :
    IConsumer<MediaProcessingFailed>,
    IConsumer<Fault<FileDiscovered>>
{
    private readonly IJobStore _jobs;
    private readonly ILogger<JobFailureConsumer> _log;

    public JobFailureConsumer(IJobStore jobs, ILogger<JobFailureConsumer> log)
    {
        _jobs = jobs;
        _log = log;
    }

    public async Task Consume(ConsumeContext<MediaProcessingFailed> context)
    {
        var m = context.Message;
        if (m.JobId is null)
        {
            _log.LogWarning("MediaProcessingFailed for {FilePath} at {Stage} with no job id: {Error}",
                m.FilePath, m.Stage, m.Error);
            return;
        }
        await _jobs.MarkFailedAsync(m.JobId.Value, m.Stage, m.Error, context.CancellationToken);
    }

    public async Task Consume(ConsumeContext<Fault<FileDiscovered>> context)
    {
        var fault = context.Message;
        var error = fault.Exceptions is { Length: > 0 }
            ? fault.Exceptions[0].Message
            : "retry exhausted";
        await _jobs.MarkFailedAsync(fault.Message.JobId, "ingest", error, context.CancellationToken);
    }
}
