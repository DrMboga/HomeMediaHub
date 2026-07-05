namespace Data.Entities;

public sealed class SeriesMedia
{
    public Guid SeriesId { get; set; }
    public Guid MediaFileId { get; set; }
    public PhotoSeries? Series { get; set; }
    public MediaFile? MediaFile { get; set; }
}
