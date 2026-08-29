using Contracts.Messages;
using Data.Entities;
using MassTransit;
using MediaWorker.Sagas;
using MediaWorker.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MediaWorker.Consumers;

/// <summary>
/// The save-early heart of the pipeline (architecture §2). For each <see cref="FileDiscovered"/>:
/// hash → dedup → convert (HEIC/video) → metadata → thumbnail → SAVE the media row (visible in the
/// gallery immediately) → transition the job to <c>enriching</c> → publish
/// <see cref="MediaEnrichmentRequested"/> to start the saga. Prefetch 2 / concurrency 2 — one SATA
/// spindle, more concurrency is seek thrash. Job-status transitions go through <see cref="IJobStore"/>.
/// </summary>
public sealed class IngestConsumer : IConsumer<FileDiscovered>
{
    private const string SharedTemp = "/shared-temp";

    private readonly WorkerDbContext _db;
    private readonly IJobStore _jobs;
    private readonly FileHashService _hash;
    private readonly DedupService _dedup;
    private readonly FileConvertService _convert;
    private readonly MetadataService _metadata;
    private readonly ThumbnailService _thumbnail;
    private readonly ILogger<IngestConsumer> _log;

    public IngestConsumer(
        WorkerDbContext db,
        IJobStore jobs,
        FileHashService hash,
        DedupService dedup,
        FileConvertService convert,
        MetadataService metadata,
        ThumbnailService thumbnail,
        ILogger<IngestConsumer> log)
    {
        _db = db;
        _jobs = jobs;
        _hash = hash;
        _dedup = dedup;
        _convert = convert;
        _metadata = metadata;
        _thumbnail = thumbnail;
        _log = log;
    }

    public async Task Consume(ConsumeContext<FileDiscovered> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;
        Guid? savedMediaId = null;

        try
        {
            await _jobs.MarkProcessingAsync(msg.JobId, ct);

            // Retry path: a media row already exists for this path → skip re-ingest, regenerate the
            // work file/thumbnail if missing, and go straight to enrichment.
            if (msg.Reprocess)
            {
                var existing = await _db.MediaFiles
                    .FirstOrDefaultAsync(f => f.FilePath == msg.FilePath, ct);
                if (existing is not null)
                {
                    await ReenrichAsync(context, msg.JobId, existing.Id, msg.FilePath, ct);
                    return;
                }
            }

            // 1. hash → 2. dedup
            var hash = await _hash.HashAsync(msg.FilePath, ct);
            var duplicateId = await _dedup.FindByHashAsync(hash, ct);
            if (duplicateId is not null)
            {
                await _jobs.MarkSkippedDuplicateAsync(msg.JobId, duplicateId.Value, ct);
                return;
            }

            // 3. deliberately deleted? Checked after the duplicate lookup, because a live photo
            // with this hash is the stronger answer — the content is in the library regardless of
            // what was deleted before. Reprocess is not exempt: a retry of a deleted photo must
            // stay deleted. The file itself is left on disk, so every scan rediscovers it and
            // lands here again — the same harmless churn the 992 known duplicates already cause.
            if (await _dedup.WasDeletedAsync(hash, ct))
            {
                _log.LogInformation(
                    "Skipping {Path}: its content was deleted from the library", msg.FilePath);
                await _jobs.MarkSkippedDeletedAsync(msg.JobId, ct);
                return;
            }

            var mediaFileId = Guid.NewGuid();
            var (analyzePath, workPath) = await ConvertIfNeededAsync(msg.FilePath, mediaFileId, ct);
            savedMediaId = mediaFileId;

            // 3. metadata (from the original for EXIF; dimensions from the converted file) → 4. thumbnail
            var originalForExif = analyzePath == msg.FilePath ? null : msg.FilePath;
            var meta = await _metadata.ExtractAsync(analyzePath, originalForExif, ct);
            var thumbnailPath = await _thumbnail.GenerateAsync(analyzePath, mediaFileId, ct);

            // 5. SAVE — media row is now browsable; AI + geo fill in later via the saga.
            var file = BuildMediaFile(mediaFileId, msg, hash, meta, thumbnailPath);
            _db.MediaFiles.Add(file);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another consumer saved the same path/hash concurrently — treat as duplicate.
                _db.ChangeTracker.Clear();
                TryDelete(workPath);
                await _jobs.MarkSkippedDuplicateAsync(msg.JobId, mediaFileId, ct);
                return;
            }

            await _jobs.MarkEnrichingAsync(msg.JobId, mediaFileId, ct);

            await context.Publish(new MediaEnrichmentRequested(
                CorrelationId:    mediaFileId,
                JobId:            msg.JobId,
                OriginalFilePath: msg.FilePath,
                AnalyzeFilePath:  analyzePath,
                WorkFilePath:     workPath,
                HasGps:           meta.Lat.HasValue && meta.Lon.HasValue,
                Lat:              meta.Lat,
                Lon:              meta.Lon));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Ingest failed for {FilePath} (job {JobId})", msg.FilePath, msg.JobId);
            await context.Publish(new MediaProcessingFailed(
                JobId:       msg.JobId,
                MediaFileId: savedMediaId,
                FilePath:    msg.FilePath,
                Stage:       "ingest",
                Error:       ex.Message,
                OccurredAt:  DateTimeOffset.UtcNow));
        }
    }

    // Reprocess of an already-saved file: regenerate the temp file/thumbnail if missing, then
    // re-publish MediaEnrichmentRequested (the saga tolerates being re-triggered).
    private async Task ReenrichAsync(
        ConsumeContext context, Guid jobId, Guid mediaFileId, string filePath, CancellationToken ct)
    {
        var kind = MediaFileType.Classify(filePath);
        string analyzePath;
        string? workPath = null;

        if (kind is MediaKind.Heic or MediaKind.Video)
        {
            workPath = Path.Combine(SharedTemp, $"{mediaFileId}.jpg");
            if (!File.Exists(workPath))
            {
                if (kind == MediaKind.Heic) await _convert.ConvertHeicAsync(filePath, workPath, ct);
                else await _convert.ExtractVideoFrameAsync(filePath, workPath, ct);
            }
            analyzePath = workPath;
        }
        else
        {
            analyzePath = filePath;
        }

        var thumbnailPath = $"/thumbnails/{mediaFileId}.webp";
        if (!File.Exists(thumbnailPath))
            await _thumbnail.GenerateAsync(analyzePath, mediaFileId, ct);

        var meta = await _metadata.ExtractAsync(
            analyzePath, analyzePath == filePath ? null : filePath, ct);

        await _jobs.MarkEnrichingAsync(jobId, mediaFileId, ct);

        await context.Publish(new MediaEnrichmentRequested(
            CorrelationId:    mediaFileId,
            JobId:            jobId,
            OriginalFilePath: filePath,
            AnalyzeFilePath:  analyzePath,
            WorkFilePath:     workPath,
            HasGps:           meta.Lat.HasValue && meta.Lon.HasValue,
            Lat:              meta.Lat,
            Lon:              meta.Lon));
    }

    private async Task<(string AnalyzePath, string? WorkPath)> ConvertIfNeededAsync(
        string filePath, Guid mediaFileId, CancellationToken ct)
    {
        switch (MediaFileType.Classify(filePath))
        {
            case MediaKind.Heic:
            {
                var work = Path.Combine(SharedTemp, $"{mediaFileId}.jpg");
                await _convert.ConvertHeicAsync(filePath, work, ct);
                return (work, work);
            }
            case MediaKind.Video:
            {
                var work = Path.Combine(SharedTemp, $"{mediaFileId}.jpg");
                await _convert.ExtractVideoFrameAsync(filePath, work, ct);
                return (work, work);
            }
            default:
                return (filePath, null);
        }
    }

    private static MediaFile BuildMediaFile(
        Guid id, FileDiscovered msg, string hash, MetadataResult meta, string thumbnailPath)
    {
        var takenAt = meta.TakenAt.HasValue
            ? DateTime.SpecifyKind(meta.TakenAt.Value, DateTimeKind.Utc)
            : (DateTime?)null;

        var file = new MediaFile
        {
            Id          = id,
            FilePath    = msg.FilePath,
            FileHash    = hash,
            Source      = msg.Source,
            MimeType    = meta.MimeType,
            FileSize    = meta.FileSize,
            Width       = meta.Width,
            Height      = meta.Height,
            DurationSec = meta.DurationSec,
            TakenAt     = takenAt,
            CreatedAt   = DateTime.UtcNow,
            Thumbnail   = new MediaThumbnail { MediaFileId = id, ThumbnailPath = thumbnailPath },
        };

        if (meta.Lat is not null || meta.Lon is not null ||
            meta.CameraMake is not null || meta.CameraModel is not null)
        {
            file.Metadata = new MediaMetadata
            {
                MediaFileId = id,
                Latitude    = meta.Lat,
                Longitude   = meta.Lon,
                CameraMake  = meta.CameraMake,
                CameraModel = meta.CameraModel,
            };
        }

        return file;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort temp cleanup */ }
    }
}
