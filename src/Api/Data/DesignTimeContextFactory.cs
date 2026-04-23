using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Api.Data;

// Used only by `dotnet ef migrations add` — not loaded at runtime.
internal sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=homemediahub_db;Username=postgres;Password=design-time")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(opts);
    }
}
