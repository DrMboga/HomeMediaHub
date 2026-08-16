using MediaWorker.Sagas;
using Microsoft.EntityFrameworkCore;

namespace MediaWorker.Services;

/// <summary>
/// What a scan found: the files to queue, plus the non-media it refused to queue. The skip counts
/// are reported rather than swallowed — silently dropping files is how you end up unable to
/// explain a discrepancy between the library and the disk.
/// </summary>
/// <param name="Files">New media files, sorted, excluding anything already indexed or in flight.</param>
/// <param name="SkippedSidecars">macOS AppleDouble <c>._*</c> resource forks.</param>
/// <param name="SkippedEmpty">Zero-length files.</param>
public sealed record ScanDiscovery(List<string> Files, int SkippedSidecars, int SkippedEmpty);

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

    // macOS writes a resource-fork companion next to each file when copying to a non-HFS volume.
    // They carry a media extension and are always 4096 bytes of AppleDouble header — never an
    // image. 14 of them failed every scan of the prod library (docs/backlog.md buckets 2 and 5).
    private const string AppleDoublePrefix = "._";

    private readonly WorkerDbContext _db;
    public ScanService(WorkerDbContext db) => _db = db;

    public async Task<ScanDiscovery> DiscoverAsync(string path, CancellationToken ct)
    {
        var indexed = await _db.MediaFiles
            .Select(f => f.FilePath)
            .ToHashSetAsync(ct);

        var active = await _db.ProcessingJobs
            .Where(j => ActiveStatuses.Contains(j.Status))
            .Select(j => j.FilePath)
            .ToHashSetAsync(ct);

        var files = new List<string>();
        var skippedSidecars = 0;
        var skippedEmpty = 0;

        // DirectoryInfo.EnumerateFiles rather than Directory.EnumerateFiles: Length comes from the
        // enumeration itself, so the zero-length check costs no extra syscall per file.
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (!SupportedExtensions.Contains(file.Extension)) continue;

            if (file.Name.StartsWith(AppleDoublePrefix, StringComparison.Ordinal))
            {
                skippedSidecars++;
                continue;
            }

            if (file.Length == 0)
            {
                skippedEmpty++;
                continue;
            }

            if (indexed.Contains(file.FullName) || active.Contains(file.FullName)) continue;

            files.Add(file.FullName);
        }

        files.Sort();   // Comparer<string>.Default — same ordering the old OrderBy(f => f) gave
        return new ScanDiscovery(files, skippedSidecars, skippedEmpty);
    }
}
