using Contracts.Messages;
using GeoService.Features.Geocoding;
using GeoService.Features.GeoLookup;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GeoService.Tests.Features.GeoLookup;

public sealed class GeoLookupConsumerTests
{
    private static ConsumeContext<GeoLookupRequested> Ctx(
        GeoLookupRequested msg, out Func<GeoLookupCompleted?> published)
    {
        var ctx = Substitute.For<ConsumeContext<GeoLookupRequested>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);

        GeoLookupCompleted? captured = null;
        ctx.When(x => x.Publish(Arg.Any<GeoLookupCompleted>(), Arg.Any<CancellationToken>()))
            .Do(ci => captured = ci.Arg<GeoLookupCompleted>());
        published = () => captured;

        return ctx;
    }

    [Fact]
    public async Task Consume_MediatorSucceeds_PublishesSuccessWithAddress()
    {
        var correlationId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ReverseGeocodeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReverseGeocodeResult("London, UK", Cached: false));

        var msg = new GeoLookupRequested(correlationId, 51.5, -0.1167);
        var ctx = Ctx(msg, out var published);

        await new GeoLookupConsumer(mediator, NullLogger<GeoLookupConsumer>.Instance).Consume(ctx);

        var result = published();
        Assert.NotNull(result);
        Assert.Equal(correlationId, result!.CorrelationId);
        Assert.True(result.Success);
        Assert.Equal("London, UK", result.Address);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Consume_MediatorThrows_PublishesFailureWithError()
    {
        var correlationId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ReverseGeocodeRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ReverseGeocodeResult>(new HttpRequestException("Nominatim unreachable")));

        var msg = new GeoLookupRequested(correlationId, 51.5, -0.1167);
        var ctx = Ctx(msg, out var published);

        await new GeoLookupConsumer(mediator, NullLogger<GeoLookupConsumer>.Instance).Consume(ctx);

        var result = published();
        Assert.NotNull(result);
        Assert.Equal(correlationId, result!.CorrelationId);
        Assert.False(result.Success);
        Assert.Null(result.Address);
        Assert.Equal("Nominatim unreachable", result.Error);
    }
}
