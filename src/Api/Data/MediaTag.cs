namespace Api.Data;

public sealed class MediaTag
{
    public Guid Id { get; set; }
    public Guid MediaFileId { get; set; }
    public string Tag { get; set; } = "";
    public float Confidence { get; set; }
    public string Model { get; set; } = "";
}
