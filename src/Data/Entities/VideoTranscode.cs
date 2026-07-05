namespace Data.Entities;

public sealed class VideoTranscode
{
    public Guid MediaFileId { get; set; }
    public string TranscodePath { get; set; } = "";
    public string Codec { get; set; } = "h264";
    public string Container { get; set; } = "mp4";
    public long? FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
}
