namespace Data.Entities;

public sealed class MediaThumbnail
{
    public Guid MediaFileId { get; set; }
    public string ThumbnailPath { get; set; } = "";
}
