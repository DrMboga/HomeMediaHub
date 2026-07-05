using System.Text.Json;
using Contracts.Messages;
using MassTransit;

namespace MediaWorker.Sagas;

/// <summary>
/// Coordinates the parallel AI + geo enrichment of one media file (architecture §6). AI and geo
/// finish in any order; the saga fans them out, stores each result as it lands, and — once both
/// legs are done — publishes <see cref="ApplyEnrichmentRequested"/> and finalizes (row deleted).
/// The always-publish AI contract guarantees the AI leg always reports, so the saga always
/// completes without a scheduler timeout.
/// </summary>
public sealed class EnrichmentStateMachine : MassTransitStateMachine<EnrichmentState>
{
    public State Enriching { get; private set; } = null!;

    public Event<MediaEnrichmentRequested> EnrichmentRequested { get; private set; } = null!;
    public Event<AiAnalysisCompleted>      AiCompleted         { get; private set; } = null!;
    public Event<GeoLookupCompleted>       GeoCompleted        { get; private set; } = null!;

    public EnrichmentStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => EnrichmentRequested, x => x.CorrelateById(ctx => ctx.Message.CorrelationId));
        // Duplicate AI/geo deliveries after the saga finalizes are dropped silently.
        Event(() => AiCompleted, x =>
        {
            x.CorrelateById(ctx => ctx.Message.CorrelationId);
            x.OnMissingInstance(m => m.Discard());
        });
        Event(() => GeoCompleted, x =>
        {
            x.CorrelateById(ctx => ctx.Message.CorrelationId);
            x.OnMissingInstance(m => m.Discard());
        });

        Initially(
            When(EnrichmentRequested)
                .Then(InitializeInstance)
                .ThenAsync(PublishRequests)
                .TransitionTo(Enriching));

        During(Enriching,
            When(AiCompleted)
                .Then(StoreAiResult)
                .If(ctx => ctx.Saga.GeoDone, both => both
                    .ThenAsync(ApplyAndFinalize)
                    .Finalize()),
            When(GeoCompleted)
                .Then(StoreGeoResult)
                .If(ctx => ctx.Saga.AiDone, both => both
                    .ThenAsync(ApplyAndFinalize)
                    .Finalize()),
            // Retry re-trigger (Reprocess): refresh context, re-publish only the unfinished leg(s).
            When(EnrichmentRequested)
                .ThenAsync(RepublishUnfinished));

        SetCompletedWhenFinalized();
    }

    private static void InitializeInstance(BehaviorContext<EnrichmentState, MediaEnrichmentRequested> ctx)
    {
        var m = ctx.Message;
        ctx.Saga.JobId           = m.JobId;
        ctx.Saga.AnalyzeFilePath = m.AnalyzeFilePath;
        ctx.Saga.WorkFilePath    = m.WorkFilePath;
        ctx.Saga.HasGps          = m.HasGps;
        ctx.Saga.Lat             = m.Lat;
        ctx.Saga.Lon             = m.Lon;
        ctx.Saga.StartedAt       = DateTime.UtcNow;

        // No GPS → the geo leg is a no-op that's already "done".
        ctx.Saga.GeoDone = !m.HasGps;
    }

    private static async Task PublishRequests(BehaviorContext<EnrichmentState, MediaEnrichmentRequested> ctx)
    {
        await ctx.Publish(new AiAnalysisRequested(ctx.Saga.CorrelationId, ctx.Saga.AnalyzeFilePath));
        if (ctx.Saga.HasGps)
            await ctx.Publish(new GeoLookupRequested(ctx.Saga.CorrelationId, ctx.Saga.Lat!.Value, ctx.Saga.Lon!.Value));
    }

    private static void StoreAiResult(BehaviorContext<EnrichmentState, AiAnalysisCompleted> ctx)
    {
        var m = ctx.Message;
        ctx.Saga.AiDone           = true;
        ctx.Saga.AiSuccess        = m.Success;
        ctx.Saga.TagsJson         = JsonSerializer.Serialize(m.Tags ?? []);
        ctx.Saga.EmbeddingJson    = JsonSerializer.Serialize(m.Embedding ?? []);
        ctx.Saga.OriginClass      = m.OriginClass;
        ctx.Saga.OriginConfidence = m.OriginConfidence;
        ctx.Saga.AiError          = m.Error;
    }

    private static void StoreGeoResult(BehaviorContext<EnrichmentState, GeoLookupCompleted> ctx)
    {
        var m = ctx.Message;
        ctx.Saga.GeoDone   = true;
        ctx.Saga.GeoSuccess = m.Success;
        ctx.Saga.Address   = m.Address;
        ctx.Saga.GeoError  = m.Error;
    }

    private static async Task ApplyAndFinalize<T>(BehaviorContext<EnrichmentState, T> ctx) where T : class
    {
        var s = ctx.Saga;
        var tags = s.TagsJson is null ? [] : JsonSerializer.Deserialize<AiTag[]>(s.TagsJson) ?? [];
        var embedding = s.EmbeddingJson is null ? [] : JsonSerializer.Deserialize<float[]>(s.EmbeddingJson) ?? [];

        await ctx.Publish(new ApplyEnrichmentRequested(
            MediaFileId:      s.CorrelationId,
            JobId:            s.JobId,
            WorkFilePath:     s.WorkFilePath,
            AiSuccess:        s.AiSuccess,
            Tags:             tags,
            Embedding:        embedding,
            OriginClass:      s.OriginClass,
            OriginConfidence: s.OriginConfidence,
            AiError:          s.AiError,
            GeoSuccess:       s.GeoSuccess,
            Address:          s.Address,
            GeoError:         s.GeoError));
    }

    private static async Task RepublishUnfinished(BehaviorContext<EnrichmentState, MediaEnrichmentRequested> ctx)
    {
        var m = ctx.Message;
        // Refresh the request context in case the retry regenerated the work file.
        ctx.Saga.JobId           = m.JobId;
        ctx.Saga.AnalyzeFilePath = m.AnalyzeFilePath;
        ctx.Saga.WorkFilePath    = m.WorkFilePath;
        ctx.Saga.HasGps          = m.HasGps;
        ctx.Saga.Lat             = m.Lat;
        ctx.Saga.Lon             = m.Lon;

        if (!ctx.Saga.AiDone)
            await ctx.Publish(new AiAnalysisRequested(ctx.Saga.CorrelationId, ctx.Saga.AnalyzeFilePath));
        if (m.HasGps && !ctx.Saga.GeoDone)
            await ctx.Publish(new GeoLookupRequested(ctx.Saga.CorrelationId, m.Lat!.Value, m.Lon!.Value));
    }
}
