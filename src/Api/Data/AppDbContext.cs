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

    public DbSet<MediaFileRecord> MediaFiles => Set<MediaFileRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // All Identity tables land in the 'identity' schema
        builder.HasDefaultSchema("identity");

        // media_files is owned by Phase 1 init SQL — read here, never migrated
        builder.Entity<MediaFileRecord>(e =>
        {
            e.ToTable("media_files", schema: "public", t => t.ExcludeFromMigrations());
            e.HasKey(x => x.Id);
        });
    }
}
