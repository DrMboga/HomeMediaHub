namespace Api.Data;

public sealed class MediaThumbnail
{
    public Guid MediaFileId { get; set; }
    public string ThumbnailPath { get; set; } = "";
}
