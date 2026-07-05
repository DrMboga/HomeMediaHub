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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema("identity");

        // Media schema is defined once in the shared Data library. excludeFromMigrations stays
        // true until Step 2 flips it — EF has not yet taken over the media tables.
        MediaModelConfiguration.ConfigureMediaEntities(builder, excludeFromMigrations: true);
    }
}
