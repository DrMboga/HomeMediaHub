namespace Contracts.Messages;

/// <summary>Requests reverse geocoding; consumed by GeoService's GeoLookupConsumer.</summary>
public record GeoLookupRequested(Guid CorrelationId, double Lat, double Lon);

/// <summary>Result of reverse geocoding, fed back into the enrichment saga.</summary>
public record GeoLookupCompleted(Guid CorrelationId, bool Success, string? Address, string? Error);
