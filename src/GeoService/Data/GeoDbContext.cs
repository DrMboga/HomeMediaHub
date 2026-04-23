using GeoService.Models;
using Microsoft.EntityFrameworkCore;

namespace GeoService.Data;

public sealed class GeoDbContext : DbContext
{
    public GeoDbContext(DbContextOptions<GeoDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    public DbSet<GeoCacheEntry> GeoCacheEntries => Set<GeoCacheEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<GeoCacheEntry>(e =>
        {
            e.ToTable("geo_cache");
            e.HasKey(g => new { g.Lat, g.Lon });
        });
    }
}
