namespace Api.Data;

// Read-only projection of public.media_files used by tool endpoints.
// Table is owned by init SQL; excluded from EF Core migrations.
public sealed class MediaFileRecord
{
    public Guid Id { get; init; }
    public string FilePath { get; init; } = "";
    public string FileHash { get; init; } = "";
    public bool IsDeleted { get; init; }
}
