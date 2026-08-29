namespace MediaWorker.Services;

/// <summary>
/// Scan-time filters that differ per environment, so they are configured rather than hardcoded:
/// stage has no Immich at all, prod's library root contains Immich's entire media tree.
/// </summary>
/// <param name="ExcludeDirs">
/// Absolute directory paths the walk must not descend into, already normalised. Absolute paths
/// rather than bare directory names on purpose — a name rule (<c>thumbs</c>, <c>upload</c>, …)
/// would also swallow a real <c>Photo/2019/upload/</c>, and the log line could not say which tree
/// it skipped.
/// </param>
/// <param name="MinFileAge">
/// How long a file must have been untouched before it is eligible. Guards against hashing a file
/// that is still being written — a Samba copy, or an upload landing straight on its final path.
/// <see cref="TimeSpan.Zero"/> disables the check.
/// </param>
public sealed record ScanOptions(IReadOnlyList<string> ExcludeDirs, TimeSpan MinFileAge)
{
    /// <summary>Excludes nothing and skips nothing — today's behaviour, and the test default.</summary>
    public static readonly ScanOptions Empty = new([], TimeSpan.Zero);

    public static ScanOptions Create(IEnumerable<string>? excludeDirs, TimeSpan minFileAge) =>
        new(Normalise(excludeDirs), minFileAge < TimeSpan.Zero ? TimeSpan.Zero : minFileAge);

    // Rooted and stripped of trailing separators so prefix matching in ScanService is a plain
    // string comparison. A path that cannot be rooted (empty, or garbage the OS rejects) is
    // dropped rather than thrown on: a typo in one exclude entry must not take the worker down.
    private static IReadOnlyList<string> Normalise(IEnumerable<string>? dirs)
    {
        if (dirs is null) return [];

        var result = new List<string>();
        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                result.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir.Trim())));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Skipped silently here; ScanService logs the list it ended up with.
            }
        }
        return result;
    }
}
