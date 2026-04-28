namespace Api.Data;

public sealed class MediaMetadata
{
    public Guid MediaFileId { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Address { get; set; }
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
}
