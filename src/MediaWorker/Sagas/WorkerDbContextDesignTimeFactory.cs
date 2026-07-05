using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MediaWorker.Sagas;

// Used only by `dotnet ef migrations add` — not loaded at runtime.
// The worker records its migrations in a separate history table so it never collides with the
// Api's migrations against the same database.
internal sealed class WorkerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<WorkerDbContext>
{
    public WorkerDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<WorkerDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=homemediahub_db;Username=postgres;Password=design-time",
                npgsql => npgsql
                    .UseVector()
                    .MigrationsHistoryTable("__ef_migrations_history_worker", "public"))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new WorkerDbContext(opts);
    }
}
