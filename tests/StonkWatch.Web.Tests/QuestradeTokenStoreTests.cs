using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

    public Task InitializeAsync() => _fixture.ResetAsync();

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
        StonkWatchDbContext db,
        IDataProtectionProvider keys,
        ILogger<QuestradeTokenStore>? logger = null) =>
        new(db, keys, _time, logger ?? NullLogger<QuestradeTokenStore>.Instance);

    private static Task<string> ReadCiphertextAsync(StonkWatchDbContext db) =>
        db.Database
            .SqlQueryRaw<string>("SELECT protected_refresh_token AS \"Value\" FROM questrade_token")
            .SingleAsync();

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
            var stored = await ReadCiphertextAsync(db);

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
        const string plaintext = "written-with-the-old-keys";

        await using (var db = _fixture.CreateContext())
        {
            await NewStore(db, Keys("primary")).SaveAsync(plaintext);
        }

        await using (var db = _fixture.CreateContext())
        {
            var ciphertext = await ReadCiphertextAsync(db);
            var log = new CapturingLogger<QuestradeTokenStore>();

            // Stands in for Data Protection keys regenerated on restart, which is what
            // happens when DataProtectionKeysPath is not configured.
            var read = await NewStore(db, Keys("regenerated"), log).ReadAsync();
            Assert.Null(read);

            // That warning is the only runtime signal that the user's Questrade connection
            // just died, so it has to actually fire — and say what to do about it.
            var warning = Assert.Single(log.Entries);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Contains("DataProtectionKeysPath", warning.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(plaintext, warning.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(ciphertext, warning.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_protector_purpose_string_is_stable()
    {
        // Pinned from outside the class: every other test protects and unprotects through the
        // same store, so both sides move together and a renamed purpose stays invisible.
        // Changing it orphans the token already in the user's database.
        var keys = Keys("primary");
        var ciphertext = keys
            .CreateProtector("StonkWatch.Questrade.RefreshToken")
            .Protect("seeded-by-hand");

        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO questrade_token (id, protected_refresh_token, updated_at)
             VALUES (1, {ciphertext}, now())
             """);

        Assert.Equal("seeded-by-hand", await NewStore(db, keys).ReadAsync());
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
