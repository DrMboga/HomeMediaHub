namespace Data.Entities;

public sealed class PhotoSeries
{
    public Guid Id { get; set; }
    public string SeriesType { get; set; } = "";  // 'burst' | 'event'
    public DateTime? TakenAtStart { get; set; }
    public DateTime? TakenAtEnd { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public Guid? CoverMediaId { get; set; }
    public List<SeriesMedia> SeriesMediaItems { get; set; } = [];
}
