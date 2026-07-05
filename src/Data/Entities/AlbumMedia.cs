namespace Data.Entities;

public sealed class AlbumMedia
{
    public Guid AlbumId { get; set; }
    public Guid MediaFileId { get; set; }
    public DateTime AddedAt { get; set; }
    public Album? Album { get; set; }
    public MediaFile? MediaFile { get; set; }
}
