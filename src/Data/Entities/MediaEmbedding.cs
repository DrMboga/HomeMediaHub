using Pgvector;

namespace Data.Entities;

public sealed class MediaEmbedding
{
    public Guid MediaFileId { get; set; }
    public Vector Embedding { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
