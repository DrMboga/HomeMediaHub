using Pgvector;

namespace Api.Data;

public sealed class MediaEmbedding
{
    public Guid MediaFileId { get; set; }
    public Vector Embedding { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
