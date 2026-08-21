using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services.MarketData.Questrade;

namespace StonkWatch.Web.Tests;

/// <summary>
/// The refresh token is the only way back into Questrade without a manual re-authorization,
/// so these tests pin the two properties that protect it: it round-trips exactly, and it is
/// never readable from the table.
/// </summary>
[Collection(PostgresCollection.Name)]
public class QuestradeTokenStoreTests : IAsyncLifetime, IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 19, 13, 30, 0, TimeSpan.Zero));
    private readonly string _keyRoot = Path.Combine(
        Path.GetTempPath(), "stonkwatch-dp-" + Guid.NewGuid().ToString("N"));

    public QuestradeTokenStoreTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM questrade_token;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Directory.Exists(_keyRoot))
        {
            Directory.Delete(_keyRoot, recursive: true);
        }
    }

    /// <summary>
    /// A provider with its own key ring on disk. Two different names produce two key rings,
    /// which is how the "keys were regenerated" case is reproduced.
    /// </summary>
    private IDataProtectionProvider Keys(string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_keyRoot, name));
        return DataProtectionProvider.Create(directory);
    }

    private QuestradeTokenStore NewStore(
        StonkWatchDbContext db, IDataProtectionProvider keys) =>
        new(db, keys, _time, NullLogger<QuestradeTokenStore>.Instance);

    [Fact]
    public async Task Saving_then_reading_returns_the_same_token()
    {
        var keys = Keys("primary");

        await using (var db = _fixture.CreateContext())
        {
            await NewStore(db, keys).SaveAsync("refresh-token-one");
        }

        await using (var db = _fixture.CreateContext())
        {
            var read = await NewStore(db, keys).ReadAsync();
            Assert.Equal("refresh-token-one", read);
        }
    }

    [Fact]
    public async Task The_token_is_not_stored_as_plaintext()
    {
        const string plaintext = "s3cret-questrade-refresh-token";
        var keys = Keys("primary");

        await using (var db = _fixture.CreateContext())
        {
            await NewStore(db, keys).SaveAsync(plaintext);
        }

        await using (var db = _fixture.CreateContext())
        {
            var stored = await db.Database
                .SqlQueryRaw<string>("SELECT protected_refresh_token AS \"Value\" FROM questrade_token")
                .SingleAsync();

            Assert.DoesNotContain(plaintext, stored, StringComparison.Ordinal);
            Assert.NotEmpty(stored);
        }
    }

    [Fact]
    public async Task Saving_twice_keeps_exactly_one_row()
    {
        var keys = Keys("primary");

        await using (var db = _fixture.CreateContext())
        {
            var store = NewStore(db, keys);
            await store.SaveAsync("first");
            await store.SaveAsync("second");
        }

        await using (var db = _fixture.CreateContext())
        {
            var rows = await db.Database
                .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM questrade_token")
                .SingleAsync();

            Assert.Equal(1, rows);
            Assert.Equal("second", await NewStore(db, keys).ReadAsync());
        }
    }

    [Fact]
    public async Task A_token_that_cannot_be_decrypted_reads_as_null()
    {
        await using (var db = _fixture.CreateContext())
        {
            await NewStore(db, Keys("primary")).SaveAsync("written-with-the-old-keys");
        }

        await using (var db = _fixture.CreateContext())
        {
            // Stands in for Data Protection keys regenerated on restart, which is what
            // happens when DataProtectionKeysPath is not configured.
            var read = await NewStore(db, Keys("regenerated")).ReadAsync();
            Assert.Null(read);
        }
    }

    [Fact]
    public async Task Reading_an_empty_store_returns_null()
    {
        await using var db = _fixture.CreateContext();
        Assert.Null(await NewStore(db, Keys("primary")).ReadAsync());
    }

    [Fact]
    public async Task Saving_stamps_the_injected_clock_in_utc()
    {
        var keys = Keys("primary");

        await using (var db = _fixture.CreateContext())
        {
            await NewStore(db, keys).SaveAsync("timestamped");
        }

        await using (var db = _fixture.CreateContext())
        {
            var row = await db.QuestradeTokens.AsNoTracking().SingleAsync();
            Assert.Equal(1, row.Id);
            Assert.Equal(_time.GetUtcNow(), row.UpdatedAt);
        }
    }
}
