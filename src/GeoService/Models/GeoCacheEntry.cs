namespace GeoService.Models;

public sealed class GeoCacheEntry
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Address { get; set; } = "";
    public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.UtcNow;
}
