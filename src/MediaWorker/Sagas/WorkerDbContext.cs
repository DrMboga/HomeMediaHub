using Data;
using Data.Entities;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace MediaWorker.Sagas;

/// <summary>
/// The MediaWorker's DbContext. It maps the shared media entities (via
/// <see cref="MediaModelConfiguration.ConfigureMediaEntities"/> with
/// <c>excludeFromMigrations: true</c>) so consumers can read/write them, but owns exactly one
/// migration — <c>AddEnrichmentSaga</c> — recorded in its own history table
/// (<c>__ef_migrations_history_worker</c>). Media tables only ever change via Api migrations.
/// </summary>
public class WorkerDbContext : DbContext
{
    public WorkerDbContext(DbContextOptions<WorkerDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    public DbSet<MediaFile>       MediaFiles      => Set<MediaFile>();
    public DbSet<MediaMetadata>   MediaMetadata   => Set<MediaMetadata>();
    public DbSet<MediaThumbnail>  MediaThumbnails => Set<MediaThumbnail>();
    public DbSet<MediaTag>        MediaTags       => Set<MediaTag>();
    public DbSet<MediaEmbedding>  MediaEmbeddings => Set<MediaEmbedding>();
    public DbSet<ProcessingJob>   ProcessingJobs  => Set<ProcessingJob>();

    public DbSet<EnrichmentState> EnrichmentSagas => Set<EnrichmentState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasPostgresExtension("vector");

        // Map the media entities but leave them to the Api's migrations.
        MediaModelConfiguration.ConfigureMediaEntities(builder, excludeFromMigrations: true);

        builder.Entity<EnrichmentState>(e =>
        {
            e.ToTable("enrichment_sagas", schema: "public");
            e.HasKey(x => x.CorrelationId);
            e.Property(x => x.CorrelationId).ValueGeneratedNever();
            e.Property(x => x.CurrentState).HasMaxLength(64);
            // xmin system column as the optimistic-concurrency token — no extra column.
            e.Property(x => x.Version).IsRowVersion();
        });
    }
}
