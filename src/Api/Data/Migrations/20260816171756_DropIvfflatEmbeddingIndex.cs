using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Data.Migrations
{
    /// <summary>
    /// Drops the ivfflat index on <c>media_embeddings.embedding</c>. Semantic search now runs an
    /// exact (parallel sequential) scan.
    /// <para>
    /// Measured on prod at 188,416 embeddings, 2026-08-16 — the index was **slower and less
    /// accurate than no index at all**:
    /// </para>
    /// <list type="bullet">
    /// <item>exact scan: <b>354 ms</b> warm / 478 ms cold, recall 15/15 by definition</item>
    /// <item>ivfflat probes=1 (the live setting): 556 ms, recall <b>7/15</b> on an image vector
    ///       and <b>0/15</b> on a real text query vector</item>
    /// <item>ivfflat probes=5: 1,678 ms, 13/15 · probes=10: 1,012 ms, 14/15</item>
    /// </list>
    /// <para>
    /// Cause: `AddMediaSchema` created the index on an **empty table**, and ivfflat derives its
    /// list centroids by k-means over the rows present at build time. With no rows the 100 lists
    /// were arbitrary, and all 188K vectors were then assigned to meaningless clusters — badly
    /// unbalanced, which is why raising probes cost more time than scanning everything. The 0/15
    /// on a text vector is the modality gap compounding it: text embeddings occupy a region those
    /// centroids never modelled, so the probed list contained none of the true neighbours.
    /// </para>
    /// <para>
    /// A rebuild at <c>lists = 188</c> would have fixed the centroids, but exact search already
    /// wins on both axes at this corpus size, and it removes `lists`/`probes` tuning entirely.
    /// If the library grows enough that ~350 ms hurts, the replacement is HNSW (pgvector 0.8.6 is
    /// installed): ~99% recall at single-digit ms, minutes to build, `hnsw.ef_search` to tune.
    /// </para>
    /// <para>
    /// Note the 2026-08-07 stage finding that "ivfflat was exonerated" does **not** hold here —
    /// it was measured on 276 rows, where a 100-list index cannot do damage.
    /// </para>
    /// </summary>
    public partial class DropIvfflatEmbeddingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS public.ix_media_embeddings_embedding;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the previous schema state. Deliberately *not* a recommendation — see the
            // measurements above. If a vector index is ever wanted again, build it on a populated
            // table and size lists to the row count (rows/1000), or use HNSW instead.
            migrationBuilder.Sql(
                "CREATE INDEX ix_media_embeddings_embedding " +
                "ON public.media_embeddings " +
                "USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);");
        }
    }
}
