using Api.Models;
using Data;
using Data.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

public class AppDbContext : IdentityDbContext<AppUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    public DbSet<MediaFile>       MediaFiles        => Set<MediaFile>();
    public DbSet<MediaMetadata>   MediaMetadata     => Set<MediaMetadata>();
    public DbSet<MediaThumbnail>  MediaThumbnails   => Set<MediaThumbnail>();
    public DbSet<MediaTag>        MediaTags         => Set<MediaTag>();
    public DbSet<MediaEmbedding>  MediaEmbeddings   => Set<MediaEmbedding>();
    public DbSet<ProcessingJob>   ProcessingJobs    => Set<ProcessingJob>();
    public DbSet<Album>           Albums            => Set<Album>();
    public DbSet<AlbumMedia>      AlbumMedia        => Set<AlbumMedia>();
    public DbSet<PhotoSeries>     PhotoSeries       => Set<PhotoSeries>();
    public DbSet<SeriesMedia>     SeriesMedia       => Set<SeriesMedia>();
    public DbSet<VideoTranscode>  VideoTranscodes   => Set<VideoTranscode>();
    public DbSet<PotdSend>        PotdSends         => Set<PotdSend>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema("identity");

        // Api's AppDbContext owns the media schema via EF migrations (Phase 8 Step 2).
        builder.HasPostgresExtension("vector");
        MediaModelConfiguration.ConfigureMediaEntities(builder, excludeFromMigrations: false);
    }
}
