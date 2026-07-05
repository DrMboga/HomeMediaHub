using Data.Entities;
using MediaWorker.Sagas;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MediaWorker.Tests;

/// <summary>
/// InMemory-backed <see cref="WorkerDbContext"/> that throws a Postgres unique-violation
/// (SQLSTATE 23505) the first time a <see cref="MediaFile"/> is inserted — the in-memory provider
/// does not enforce the <c>media_files</c> unique indexes, so this simulates the concurrent-save
/// race the IngestConsumer must treat as a duplicate.
/// </summary>
internal sealed class ThrowingWorkerDbContext : WorkerDbContext
{
    private bool _thrown;

    public ThrowingWorkerDbContext(string dbName)
        : base(new DbContextOptionsBuilder<WorkerDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options)
    { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<MediaEmbedding>().Ignore(x => x.Embedding);
        builder.Entity<EnrichmentState>().Ignore(x => x.Version);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!_thrown && ChangeTracker.Entries<MediaFile>().Any(e => e.State == EntityState.Added))
        {
            _thrown = true;
            var pg = new PostgresException(
                "duplicate key value violates unique constraint \"ix_media_files_file_path\"",
                "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);
            throw new DbUpdateException("unique violation", pg);
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
