namespace Data.Entities;

/// <summary>
/// A content hash the user deliberately removed from the library. Ingest consults this before
/// saving, so a photo deleted in Review never comes back from a second copy elsewhere on disk.
/// </summary>
/// <remarks>
/// Written only by <c>BulkDeleteMediaHandler</c> — the hard delete behind the Review page.
/// <c>SoftDeleteMediaHandler</c> deliberately does not write here: it leaves the
/// <c>media_files</c> row in place, and that row already shadows every duplicate by hash.
///
/// Measured on prod 2026-08-29: the library holds 992 duplicate paths, and the first scan after
/// Phase 14 ingested 167 of them as new photos because their indexed twin had been deleted through
/// Review. Deleting a file was never the same thing as deleting a photo; this table is what makes
/// it so.
/// </remarks>
public sealed class DeletedHash
{
    /// <summary>SHA-256 of the file's contents, the same value stored in <c>media_files.file_hash</c>.</summary>
    public string FileHash { get; set; } = "";

    /// <summary>
    /// Where the deleted copy lived. Never read by the pipeline — it exists so a row in this table
    /// can be explained months later without joining to jobs that may themselves be gone.
    /// </summary>
    public string? LastFilePath { get; set; }

    public DateTime DeletedAt { get; set; }
}
