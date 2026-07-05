using Api.Data;
using Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests;

internal static class TestDbContextFactory
{
    internal static AppDbContext Create() =>
        new InMemoryAppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    // The pgvector Vector type is unsupported by the in-memory provider.
    // Ignoring the property removes it from the model without changing production behaviour.
    private sealed class InMemoryAppDbContext : AppDbContext
    {
        public InMemoryAppDbContext(DbContextOptions<AppDbContext> opts) : base(opts) { }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<MediaEmbedding>().Ignore(x => x.Embedding);
        }
    }
}
