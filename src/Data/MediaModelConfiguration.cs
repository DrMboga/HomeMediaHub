using Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Data;

/// <summary>
/// The single copy of the media-schema fluent mapping, shared by the Api's <c>AppDbContext</c>
/// (which owns the media-table migrations) and the MediaWorker's <c>WorkerDbContext</c>
/// (which maps the same entities but excludes them from its own migrations).
/// </summary>
public static class MediaModelConfiguration
{
    /// <param name="excludeFromMigrations">
    /// When true, every media table is marked <c>ExcludeFromMigrations()</c> so the owning
    /// context does not attempt to create/alter it. The Api passes false once EF takes over the
    /// schema (Step 2); the MediaWorker always passes true.
    /// </param>
    public static void ConfigureMediaEntities(ModelBuilder builder, bool excludeFromMigrations)
    {
        // Generic so each call binds the strongly-typed ToTable<TEntity> overload
        // (a non-generic Action<TableBuilder> is ambiguous against it).
        Action<TableBuilder<T>> Table<T>() where T : class =>
            t => { if (excludeFromMigrations) t.ExcludeFromMigrations(); };

        builder.Entity<MediaFile>(e =>
        {
            e.ToTable("media_files", schema: "public", Table<MediaFile>());
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.FilePath).IsUnique();
            e.HasIndex(x => x.FileHash).IsUnique();
            // Gallery lists photos newest-first; the composite backs the default
            // "not deleted, by date desc" query, the single index the unfiltered date sort.
            e.HasIndex(x => x.TakenAt).IsDescending();
            e.HasIndex(x => new { x.IsDeleted, x.TakenAt }).IsDescending(false, true);
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
            e.ToTable("media_metadata", schema: "public", Table<MediaMetadata>());
            e.HasKey(x => x.MediaFileId);
            e.HasIndex(x => new { x.Latitude, x.Longitude });  // geo map bounding-box queries
        });

        builder.Entity<MediaThumbnail>(e =>
        {
            e.ToTable("media_thumbnails", schema: "public", Table<MediaThumbnail>());
            e.HasKey(x => x.MediaFileId);
        });

        builder.Entity<MediaTag>(e =>
        {
            e.ToTable("media_tags", schema: "public", Table<MediaTag>());
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Tag);  // tag search
        });

        builder.Entity<MediaEmbedding>(e =>
        {
            e.ToTable("media_embeddings", schema: "public", Table<MediaEmbedding>());
            e.HasKey(x => x.MediaFileId);
            e.Property(x => x.Embedding).HasColumnType("vector(512)");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("NOW()");
        });

        builder.Entity<ProcessingJob>(e =>
        {
            e.ToTable("processing_jobs", schema: "public", Table<ProcessingJob>());
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.ScanId, x.Status });
            e.HasIndex(x => x.StartedAt).IsDescending();  // admin jobs list, newest-first
            e.Property(x => x.StartedAt).HasDefaultValueSql("NOW()");
            e.Property(x => x.RetryCount).HasDefaultValue(0);
        });

        builder.Entity<Album>(e =>
        {
            e.ToTable("albums", schema: "public", Table<Album>());
            e.HasKey(x => x.Id);
            e.HasMany(a => a.AlbumMediaItems)
             .WithOne(am => am.Album)
             .HasForeignKey(am => am.AlbumId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("NOW()");
        });

        builder.Entity<AlbumMedia>(e =>
        {
            e.ToTable("album_media", schema: "public", Table<AlbumMedia>());
            e.HasKey(x => new { x.AlbumId, x.MediaFileId });
            e.HasOne(am => am.MediaFile)
             .WithMany()
             .HasForeignKey(am => am.MediaFileId);
            e.Property(x => x.AddedAt).HasDefaultValueSql("NOW()");
        });

        builder.Entity<PhotoSeries>(e =>
        {
            e.ToTable("photo_series", schema: "public", Table<PhotoSeries>());
            e.HasKey(x => x.Id);
            e.HasMany(s => s.SeriesMediaItems)
             .WithOne(sm => sm.Series)
             .HasForeignKey(sm => sm.SeriesId);
        });

        builder.Entity<SeriesMedia>(e =>
        {
            e.ToTable("series_media", schema: "public", Table<SeriesMedia>());
            e.HasKey(x => new { x.SeriesId, x.MediaFileId });
            e.HasOne(sm => sm.MediaFile)
             .WithMany()
             .HasForeignKey(sm => sm.MediaFileId);
        });

        builder.Entity<VideoTranscode>(e =>
        {
            e.ToTable("video_transcodes", schema: "public", Table<VideoTranscode>());
            e.HasKey(x => x.MediaFileId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("NOW()");
        });

        builder.Entity<PotdSend>(e =>
        {
            e.ToTable("potd_sends", schema: "public", Table<PotdSend>());
            // The key is the idempotency guard — see PotdSend. No extra index: the table
            // gains one row a day and is only ever read by PK or "the last few, newest first".
            e.HasKey(x => x.SentOn);
            e.Property(x => x.SentAt).HasDefaultValueSql("NOW()");
        });
    }
}
