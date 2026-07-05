namespace Data.Entities;

public sealed class Album
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<AlbumMedia> AlbumMediaItems { get; set; } = [];
}
