using MediaWorker.Sagas;
using Microsoft.EntityFrameworkCore;

namespace MediaWorker.Services;

/// <summary>
/// Enumerates a directory for supported media, excluding files already catalogued in
/// <c>media_files</c> or with an active <c>processing_job</c> (ported from the old
/// <c>ScanDirectoryHandler</c>, extended with the active-job exclusion per architecture §2/§7).
/// </summary>
public sealed class ScanService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".mp4", ".mov", ".m4v", ".avi" };

    private static readonly string[] ActiveStatuses = ["queued", "processing", "enriching"];

    private readonly WorkerDbContext _db;
    public ScanService(WorkerDbContext db) => _db = db;

    public async Task<List<string>> DiscoverAsync(string path, CancellationToken ct)
    {
        var indexed = await _db.MediaFiles
            .Select(f => f.FilePath)
            .ToHashSetAsync(ct);

        var active = await _db.ProcessingJobs
            .Where(j => ActiveStatuses.Contains(j.Status))
            .Select(j => j.FilePath)
            .ToHashSetAsync(ct);

        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !indexed.Contains(f) && !active.Contains(f))
            .OrderBy(f => f)
            .ToList();
    }
}
