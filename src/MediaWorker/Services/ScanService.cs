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
/// <param name="SkippedTooRecent">Files written within <see cref="ScanOptions.MinFileAge"/> — possibly still being written.</param>
/// <param name="PrunedDirectories">
/// Directories not descended into, per <see cref="ScanOptions.ExcludeDirs"/>. This counts
/// *directories*, not the files inside them: counting files would mean walking the tree the
/// exclusion exists to avoid.
/// </param>
public sealed record ScanDiscovery(
    List<string> Files,
    int SkippedSidecars,
    int SkippedEmpty,
    int SkippedTooRecent = 0,
    int PrunedDirectories = 0);

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
    private readonly ScanOptions _options;

    // ScanOptions defaults to Empty so the fourteen pre-existing tests keep constructing a plain
    // ScanService(db) and keep exercising exactly what they were written for. Production registers
    // a real instance in Program.cs.
    public ScanService(WorkerDbContext db, ScanOptions? options = null)
    {
        _db = db;
        _options = options ?? ScanOptions.Empty;
    }

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
        var skippedTooRecent = 0;
        var stats = new WalkStats();

        var cutoff = _options.MinFileAge > TimeSpan.Zero
            ? DateTime.UtcNow - _options.MinFileAge
            : (DateTime?)null;

        foreach (var file in Walk(path, stats, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (!SupportedExtensions.Contains(file.Extension)) continue;

            if (file.Name.StartsWith(AppleDoublePrefix, StringComparison.Ordinal))
            {
                skippedSidecars++;
                continue;
            }

            // Already-indexed files leave before any counter, so the skip counts only ever describe
            // files that would otherwise have been queued — the principle the sidecar counter has
            // followed since Phase 12. Found the hard way: the first prod scan reported one file
            // "written too recently" that was a long-indexed JPEG carrying a corrupt year-2262
            // mtime, which the age check saw before this line could discard it.
            if (indexed.Contains(file.FullName) || active.Contains(file.FullName)) continue;

            // Age before length, deliberately: a file that is mid-write is momentarily zero bytes,
            // and counting it as dead source data would be a lie the log then carries forward.
            if (cutoff is not null && file.LastWriteTimeUtc > cutoff)
            {
                skippedTooRecent++;
                continue;
            }

            if (file.Length == 0)
            {
                skippedEmpty++;
                continue;
            }

            files.Add(file.FullName);
        }

        files.Sort();   // Comparer<string>.Default — same ordering the old OrderBy(f => f) gave
        return new ScanDiscovery(files, skippedSidecars, skippedEmpty, skippedTooRecent, stats.PrunedDirectories);
    }

    /// <summary>Out-params cannot cross an iterator boundary; this carries the walk's counters back.</summary>
    private sealed class WalkStats
    {
        public int PrunedDirectories;
    }

    /// <summary>
    /// Depth-first walk that can prune whole directories. Replaces
    /// <c>EnumerateFiles(SearchOption.AllDirectories)</c>, which has no prune hook — filtering
    /// excluded files after the fact would still walk them, and Immich's <c>thumbs/</c> alone is
    /// two entries per photo.
    /// </summary>
    private IEnumerable<FileInfo> Walk(string root, WalkStats stats, CancellationToken ct)
    {
        // An exclude entry that names the scan root itself means "scan nothing" rather than
        // "exclude nothing" — the root is never reached by the subdirectory check below.
        if (IsExcluded(Path.GetFullPath(root)))
        {
            stats.PrunedDirectories++;
            yield break;
        }

        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            FileInfo[] dirFiles;
            DirectoryInfo[] subdirs;
            try
            {
                dirFiles = dir.GetFiles();
                subdirs  = dir.GetDirectories();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                // Matches the old behaviour: EnumerationOptions.IgnoreInaccessible defaults to true
                // for the SearchOption overload, so an unreadable subtree was already silent.
                continue;
            }

            // Yielded per directory rather than collected, so the walk stays as streaming as the
            // enumerator it replaces — 190K FileInfo instances alive at once is not free.
            foreach (var file in dirFiles) yield return file;

            foreach (var sub in subdirs)
            {
                if (IsExcluded(sub.FullName))
                {
                    stats.PrunedDirectories++;
                    continue;
                }

                // Symlinked directories are not followed. A hand-rolled walk has no cycle
                // detection, and nothing in the library is a link — so this closes a possible hang
                // rather than changing a behaviour anyone relies on.
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                stack.Push(sub);
            }
        }
    }

    // Ordinal: prod is Linux, where paths are case-sensitive and "Thumbs" genuinely is not
    // "thumbs". The separator test stops /photo/iPhone/uploads matching an exclude of
    // /photo/iPhone/upload.
    private bool IsExcluded(string fullPath)
    {
        if (_options.ExcludeDirs.Count == 0) return false;

        var normalised = Path.TrimEndingDirectorySeparator(fullPath);
        foreach (var excluded in _options.ExcludeDirs)
        {
            if (normalised.Equals(excluded, StringComparison.Ordinal)) return true;
            if (normalised.StartsWith(excluded, StringComparison.Ordinal) &&
                normalised.Length > excluded.Length &&
                normalised[excluded.Length] == Path.DirectorySeparatorChar) return true;
        }
        return false;
    }
}
