namespace Contracts.Messages;

/// <summary>Published by the API (manual trigger or ScanScheduler) to kick off a library scan.</summary>
public record ScanRequested(
    Guid ScanId,
    string Path,
    string Source,
    string TriggeredBy,
    DateTimeOffset RequestedAt);

/// <summary>Emitted by ScanConsumer once per discovered file; consumed by IngestConsumer.</summary>
public record FileDiscovered(
    Guid JobId,
    Guid ScanId,
    string FilePath,
    string Source,
    bool Reprocess,
    DateTimeOffset DiscoveredAt);
