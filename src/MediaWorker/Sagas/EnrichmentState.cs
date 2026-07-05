using MassTransit;

namespace MediaWorker.Sagas;

/// <summary>
/// Persisted saga instance (table <c>enrichment_sagas</c>). One row per in-flight media file;
/// <c>SetCompletedWhenFinalized()</c> deletes the row once enrichment is applied, so the table
/// only ever holds items still waiting on AI or geo. <c>CorrelationId</c> == MediaFileId.
/// </summary>
public sealed class EnrichmentState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = null!;

    /// <summary>Optimistic-concurrency token — mapped to Postgres <c>xmin</c> via
    /// <c>.IsRowVersion()</c> (no extra column). Absorbs the race where AI and geo results land
    /// simultaneously; the losing write is retried by the endpoint's <c>UseMessageRetry</c>.</summary>
    public uint Version { get; set; }

    public Guid JobId { get; set; }

    // Request context, kept so a re-trigger can re-publish whichever leg is unfinished.
    public string AnalyzeFilePath { get; set; } = "";
    public string? WorkFilePath { get; set; }
    public bool HasGps { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }

    // AI leg
    public bool AiDone { get; set; }
    public bool AiSuccess { get; set; }
    public string? TagsJson { get; set; }
    public string? EmbeddingJson { get; set; }
    public string? OriginClass { get; set; }
    public double? OriginConfidence { get; set; }
    public string? AiError { get; set; }

    // Geo leg
    public bool GeoDone { get; set; }
    public bool GeoSuccess { get; set; }
    public string? Address { get; set; }
    public string? GeoError { get; set; }

    public DateTime StartedAt { get; set; }
}
