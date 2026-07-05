using Data.Entities;
using MediaWorker.Sagas;
using Microsoft.EntityFrameworkCore;

namespace MediaWorker.Tests;

internal static class TestDbContextFactory
{
    internal static WorkerDbContext Create() =>
        new InMemoryWorkerDbContext(
            new DbContextOptionsBuilder<WorkerDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    internal static WorkerDbContext CreateShared(string name) =>
        new InMemoryWorkerDbContext(
            new DbContextOptionsBuilder<WorkerDbContext>()
                .UseInMemoryDatabase(name)
                .Options);

    // The pgvector Vector type and the xmin concurrency token are unsupported by the in-memory
    // provider — ignoring them removes them from the model without changing production behaviour.
    private sealed class InMemoryWorkerDbContext : WorkerDbContext
    {
        public InMemoryWorkerDbContext(DbContextOptions<WorkerDbContext> opts) : base(opts) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<MediaEmbedding>().Ignore(x => x.Embedding);
            builder.Entity<EnrichmentState>().Ignore(x => x.Version);
        }
    }
}
