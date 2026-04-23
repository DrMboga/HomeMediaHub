namespace Api.Data;

// Read-only projection of public.media_files used by the scan endpoint.
// Table is owned by init SQL; excluded from EF Core migrations.
public sealed class MediaFileRecord
{
    public Guid Id { get; init; }
    public string FilePath { get; init; } = "";
    public bool IsDeleted { get; init; }
}
