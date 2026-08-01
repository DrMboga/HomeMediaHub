using Contracts.Messages;
using GeoService.Features.Geocoding;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;

namespace GeoService.Features.GeoLookup;

public sealed class GeoLookupConsumer : IConsumer<GeoLookupRequested>
{
    private readonly IMediator _mediator;
    private readonly ILogger<GeoLookupConsumer> _logger;

    public GeoLookupConsumer(IMediator mediator, ILogger<GeoLookupConsumer> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<GeoLookupRequested> context)
    {
        var message = context.Message;

        try
        {
            var result = await _mediator.Send(
                new ReverseGeocodeRequest(message.Lat, message.Lon), context.CancellationToken);

            await context.Publish(new GeoLookupCompleted(
                message.CorrelationId, Success: true, result.Address, Error: null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reverse geocode failed for {CorrelationId}", message.CorrelationId);

            await context.Publish(new GeoLookupCompleted(
                message.CorrelationId, Success: false, Address: null, Error: ex.Message));
        }
    }
}
