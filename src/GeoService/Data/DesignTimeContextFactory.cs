using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GeoService.Data;

// Used only by `dotnet ef migrations add` — not loaded at runtime.
internal sealed class GeoDbContextDesignTimeFactory : IDesignTimeDbContextFactory<GeoDbContext>
{
    public GeoDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<GeoDbContext>()
            .UseNpgsql("Host=localhost;Database=geoservice_db;Username=geoservice;Password=design-time")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new GeoDbContext(opts);
    }
}
