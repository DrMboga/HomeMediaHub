using Contracts.Messages;
using MassTransit;

namespace MediaWorker.Consumers;

/// <summary>
/// Anti-corruption layer between the Python sidecar's plain-JSON output and typed MassTransit
/// envelopes (architecture §5). It receives <see cref="AiAnalysisCompleted"/> on the dedicated
/// raw-JSON endpoint and re-publishes it as a proper envelope the saga consumes normally. The saga
/// must never sit on the raw endpoint directly — raw JSON carries no messageType.
/// </summary>
public sealed class AiResultBridgeConsumer : IConsumer<AiAnalysisCompleted>
{
    public Task Consume(ConsumeContext<AiAnalysisCompleted> context) =>
        context.Publish(context.Message);
}
