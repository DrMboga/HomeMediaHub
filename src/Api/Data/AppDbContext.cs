using Api.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

public sealed class AppDbContext : IdentityDbContext<AppUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    public DbSet<MediaFile>       MediaFiles       => Set<MediaFile>();
    public DbSet<MediaMetadata>   MediaMetadata    => Set<MediaMetadata>();
    public DbSet<MediaThumbnail>  MediaThumbnails  => Set<MediaThumbnail>();
    public DbSet<MediaTag>        MediaTags        => Set<MediaTag>();
    public DbSet<MediaEmbedding>  MediaEmbeddings  => Set<MediaEmbedding>();
    public DbSet<ProcessingJob>   ProcessingJobs   => Set<ProcessingJob>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema("identity");

        builder.Entity<MediaFile>(e =>
        {
            e.ToTable("media_files", schema: "public", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
            e.HasOne(f => f.Metadata)
             .WithOne()
             .HasForeignKey<MediaMetadata>(m => m.MediaFileId);
            e.HasOne(f => f.Thumbnail)
             .WithOne()
             .HasForeignKey<MediaThumbnail>(t => t.MediaFileId);
            e.HasOne(f => f.Embedding)
             .WithOne()
             .HasForeignKey<MediaEmbedding>(m => m.MediaFileId);
            e.HasMany(f => f.Tags)
             .WithOne()
             .HasForeignKey(t => t.MediaFileId);
        });

        builder.Entity<MediaMetadata>(e =>
        {
            e.ToTable("media_metadata", schema: "public", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.MediaFileId);
        });

        builder.Entity<MediaThumbnail>(e =>
        {
            e.ToTable("media_thumbnails", schema: "public", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.MediaFileId);
        });

        builder.Entity<MediaTag>(e =>
        {
            e.ToTable("media_tags", schema: "public", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
        });

        builder.Entity<MediaEmbedding>(e =>
        {
            e.ToTable("media_embeddings", schema: "public", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.MediaFileId);
            e.Property(x => x.Embedding).HasColumnType("vector(512)");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("NOW()");
        });

        builder.Entity<ProcessingJob>(e =>
        {
            e.ToTable("processing_jobs", schema: "public", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
            e.Property(x => x.StartedAt).HasDefaultValueSql("NOW()");
            e.Property(x => x.RetryCount).HasDefaultValue(0);
        });
    }
}
