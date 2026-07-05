namespace Contracts.Messages;

/// <summary>Requests AI analysis of a single file. Bridged to the Python sidecar via the
/// fanout exchange "ai-analysis-requested" (SetEntityName, see architecture §4/§5).</summary>
public record AiAnalysisRequested(Guid CorrelationId, string FilePath);

/// <summary>A single tag produced by the AI sidecar.</summary>
public record AiTag(string Tag, double Confidence, string Model);

/// <summary>Result of AI analysis. The sidecar always publishes this (Success:false on any
/// error) and ACKs, so the saga always completes — inference failures never dead-letter.</summary>
public record AiAnalysisCompleted(
    Guid CorrelationId,
    bool Success,
    AiTag[] Tags,
    float[] Embedding,
    string? OriginClass,
    double? OriginConfidence,
    string? Error);
