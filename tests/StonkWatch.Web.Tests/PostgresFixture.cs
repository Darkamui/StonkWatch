using Microsoft.EntityFrameworkCore;
using StonkWatch.Web.Data;
using Testcontainers.PostgreSql;

namespace StonkWatch.Web.Tests;

/// <summary>
/// One real Postgres container for the whole test run. A real database is required rather
/// than the EF in-memory provider, which reproduces neither <c>timestamptz</c> UTC
/// enforcement nor <c>numeric(18,4)</c> rounding — the two behaviours these tests exist to pin.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public StonkWatchDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<StonkWatchDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new StonkWatchDbContext(options);
    }

    /// <summary>Empties every table so each test starts from a known state.</summary>
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE candidates, alerts, review_log, job_runs, watchlist_items, watchlist_groups "
            + "RESTART IDENTITY CASCADE;");
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
