namespace Data.Entities;

public sealed class ProcessingJob
{
    public Guid      Id            { get; set; }
    public string    FilePath      { get; set; } = "";
    public string    Source        { get; set; } = "";
    public string    Status        { get; set; } = "";
    public Guid?     ScanId        { get; set; }
    public Guid?     MediaFileId   { get; set; }
    public DateTime  StartedAt     { get; set; }
    public DateTime? CompletedAt   { get; set; }
    public string?   FailedStep    { get; set; }
    public string?   ErrorMessage  { get; set; }
    public int       RetryCount    { get; set; }
}
