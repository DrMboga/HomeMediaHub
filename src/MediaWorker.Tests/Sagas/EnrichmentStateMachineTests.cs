using Contracts.Messages;
using MassTransit;
using MassTransit.Testing;
using MediaWorker.Sagas;
using Microsoft.Extensions.DependencyInjection;

namespace MediaWorker.Tests.Sagas;

public sealed class EnrichmentStateMachineTests
{
    private static readonly AiTag[] SampleTags = [new AiTag("dog", 0.91, "yolov8n")];
    private static readonly TimeSpan RemovedTimeout = TimeSpan.FromSeconds(1);

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.AddSagaStateMachine<EnrichmentStateMachine, EnrichmentState>()
                    .InMemoryRepository();
            })
            .BuildServiceProvider(true);

    private static MediaEnrichmentRequested Start(Guid id, bool hasGps) =>
        new(
            CorrelationId:    id,
            JobId:            Guid.NewGuid(),
            OriginalFilePath: "/photo/x.jpg",
            AnalyzeFilePath:  "/photo/x.jpg",
            WorkFilePath:     null,
            HasGps:           hasGps,
            Lat:              hasGps ? 51.5 : null,
            Lon:              hasGps ? -0.12 : null);

    // The saga row is deleted on finalize (SetCompletedWhenFinalized) — removal ⟺ Exists returns null.
    private static async Task AssertFinalized(
        ISagaStateMachineTestHarness<EnrichmentStateMachine, EnrichmentState> saga, Guid id) =>
        Assert.Null(await saga.Exists(id, RemovedTimeout));

    [Fact]
    public async Task GpsPath_FansOutAiAndGeo_ThenFinalizesWhenBothComplete()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var saga = harness.GetSagaStateMachineHarness<EnrichmentStateMachine, EnrichmentState>();
        var id = Guid.NewGuid();

        await harness.Bus.Publish(Start(id, hasGps: true));
        Assert.True(await saga.Consumed.Any<MediaEnrichmentRequested>());

        Assert.NotNull(await saga.Exists(id, m => m.Enriching));
        Assert.True(await harness.Published.Any<AiAnalysisRequested>(p => p.Context.Message.CorrelationId == id));
        Assert.True(await harness.Published.Any<GeoLookupRequested>(p => p.Context.Message.CorrelationId == id));

        await harness.Bus.Publish(new AiAnalysisCompleted(id, true, SampleTags, [], "photo", 0.93, null));
        Assert.True(await saga.Consumed.Any<AiAnalysisCompleted>());
        await harness.Bus.Publish(new GeoLookupCompleted(id, true, "London, UK", null));
        Assert.True(await saga.Consumed.Any<GeoLookupCompleted>());

        Assert.True(await harness.Published.Any<ApplyEnrichmentRequested>(p =>
            p.Context.Message.MediaFileId == id &&
            p.Context.Message.AiSuccess &&
            p.Context.Message.GeoSuccess &&
            p.Context.Message.Address == "London, UK"));
        await AssertFinalized(saga, id);

        await harness.Stop();
    }

    [Fact]
    public async Task NoGpsPath_SkipsGeo_FinalizesOnAiAlone()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var saga = harness.GetSagaStateMachineHarness<EnrichmentStateMachine, EnrichmentState>();
        var id = Guid.NewGuid();

        await harness.Bus.Publish(Start(id, hasGps: false));
        Assert.True(await saga.Consumed.Any<MediaEnrichmentRequested>());

        Assert.NotNull(await saga.Exists(id, m => m.Enriching));
        Assert.True(await harness.Published.Any<AiAnalysisRequested>(p => p.Context.Message.CorrelationId == id));

        await harness.Bus.Publish(new AiAnalysisCompleted(id, true, SampleTags, [], "photo", 0.93, null));
        Assert.True(await saga.Consumed.Any<AiAnalysisCompleted>());

        Assert.True(await harness.Published.Any<ApplyEnrichmentRequested>(p =>
            p.Context.Message.MediaFileId == id && !p.Context.Message.GeoSuccess));
        await AssertFinalized(saga, id);

        // Geo was never requested for a file without GPS.
        Assert.DoesNotContain(harness.Published.Select<GeoLookupRequested>(),
            p => p.Context.Message.CorrelationId == id);

        await harness.Stop();
    }

    [Fact]
    public async Task AiFailure_StillFinalizes()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var saga = harness.GetSagaStateMachineHarness<EnrichmentStateMachine, EnrichmentState>();
        var id = Guid.NewGuid();

        await harness.Bus.Publish(Start(id, hasGps: false));
        Assert.True(await saga.Consumed.Any<MediaEnrichmentRequested>());
        Assert.NotNull(await saga.Exists(id, m => m.Enriching));

        await harness.Bus.Publish(new AiAnalysisCompleted(id, false, [], [], null, null, "unreadable image"));
        Assert.True(await saga.Consumed.Any<AiAnalysisCompleted>());

        Assert.True(await harness.Published.Any<ApplyEnrichmentRequested>(p =>
            p.Context.Message.MediaFileId == id &&
            !p.Context.Message.AiSuccess &&
            p.Context.Message.AiError == "unreadable image"));
        await AssertFinalized(saga, id);

        await harness.Stop();
    }

    [Fact]
    public async Task GeoBeforeAi_OrderIndependent_Finalizes()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var saga = harness.GetSagaStateMachineHarness<EnrichmentStateMachine, EnrichmentState>();
        var id = Guid.NewGuid();

        await harness.Bus.Publish(Start(id, hasGps: true));
        Assert.True(await saga.Consumed.Any<MediaEnrichmentRequested>());
        Assert.NotNull(await saga.Exists(id, m => m.Enriching));

        // Geo lands first — the saga must wait for AI before applying.
        await harness.Bus.Publish(new GeoLookupCompleted(id, true, "Paris, FR", null));
        Assert.True(await saga.Consumed.Any<GeoLookupCompleted>());
        Assert.NotNull(await saga.Exists(id, m => m.Enriching));

        await harness.Bus.Publish(new AiAnalysisCompleted(id, true, SampleTags, [], "photo", 0.9, null));
        Assert.True(await saga.Consumed.Any<AiAnalysisCompleted>());

        Assert.True(await harness.Published.Any<ApplyEnrichmentRequested>(p =>
            p.Context.Message.MediaFileId == id && p.Context.Message.Address == "Paris, FR"));
        await AssertFinalized(saga, id);

        await harness.Stop();
    }

    [Fact]
    public async Task DuplicateEventAfterFinalize_IsDiscarded()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var saga = harness.GetSagaStateMachineHarness<EnrichmentStateMachine, EnrichmentState>();
        var id = Guid.NewGuid();

        await harness.Bus.Publish(Start(id, hasGps: false));
        Assert.True(await saga.Consumed.Any<MediaEnrichmentRequested>());

        await harness.Bus.Publish(new AiAnalysisCompleted(id, true, SampleTags, [], "photo", 0.9, null));
        Assert.True(await saga.Consumed.Any<AiAnalysisCompleted>());
        await AssertFinalized(saga, id);

        var applyCountBefore = ApplyCount(harness, id);

        // A late duplicate must not resurrect the saga or apply twice.
        await harness.Bus.Publish(new AiAnalysisCompleted(id, true, SampleTags, [], "photo", 0.9, null));
        await Task.Delay(300); // let the (discarded) duplicate settle

        Assert.Null(await saga.Exists(id, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(applyCountBefore, ApplyCount(harness, id));

        await harness.Stop();
    }

    [Fact]
    public async Task RetriggerWhileEnriching_RepublishesOnlyUnfinishedLeg()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var saga = harness.GetSagaStateMachineHarness<EnrichmentStateMachine, EnrichmentState>();
        var id = Guid.NewGuid();

        await harness.Bus.Publish(Start(id, hasGps: true));
        Assert.True(await saga.Consumed.Any<MediaEnrichmentRequested>());
        Assert.NotNull(await saga.Exists(id, m => m.Enriching));

        // AI reports; geo still pending.
        await harness.Bus.Publish(new AiAnalysisCompleted(id, true, SampleTags, [], "photo", 0.9, null));
        Assert.True(await saga.Consumed.Any<AiAnalysisCompleted>());
        Assert.NotNull(await saga.Exists(id, m => m.Enriching));

        // Re-trigger: only the unfinished geo leg should be re-published, not AI.
        await harness.Bus.Publish(Start(id, hasGps: true));
        await WaitForCountAsync(() => GeoCount(harness, id), 2);

        Assert.Equal(2, GeoCount(harness, id));
        Assert.Equal(1, AiCount(harness, id));   // AI already done — not re-published
        Assert.NotNull(await saga.Exists(id, m => m.Enriching));

        await harness.Stop();
    }

    private static int AiCount(ITestHarness h, Guid id) =>
        h.Published.Select<AiAnalysisRequested>().Count(p => p.Context.Message.CorrelationId == id);

    private static int GeoCount(ITestHarness h, Guid id) =>
        h.Published.Select<GeoLookupRequested>().Count(p => p.Context.Message.CorrelationId == id);

    private static int ApplyCount(ITestHarness h, Guid id) =>
        h.Published.Select<ApplyEnrichmentRequested>().Count(p => p.Context.Message.MediaFileId == id);

    private static async Task WaitForCountAsync(Func<int> count, int target)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (count() < target && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }
}
