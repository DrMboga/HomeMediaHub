namespace Contracts.Messages;

/// <summary>Starts the enrichment saga. CorrelationId == MediaFileId.</summary>
public record MediaEnrichmentRequested(
    Guid CorrelationId,
    Guid JobId,
    string OriginalFilePath,
    string AnalyzeFilePath,   // /shared-temp/{id}.jpg for HEIC/video, else the original
    string? WorkFilePath,     // temp file to clean up, null if none created
    bool HasGps,
    double? Lat,
    double? Lon);

/// <summary>Emitted by the saga when AI and geo are both done; consumed by ApplyEnrichmentConsumer.</summary>
public record ApplyEnrichmentRequested(
    Guid MediaFileId,
    Guid JobId,
    string? WorkFilePath,
    bool AiSuccess,
    AiTag[] Tags,
    float[] Embedding,
    string? OriginClass,
    double? OriginConfidence,
    string? AiError,
    bool GeoSuccess,
    string? Address,
    string? GeoError);
