using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services.Watchlist;

namespace StonkWatch.Web.Tests;

[Collection(PostgresCollection.Name)]
public class WatchlistServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (WatchlistService Service, StonkWatchDbContext Db) NewService(int maxSymbols = 50)
    {
        var db = fixture.CreateContext();
        var options = Options.Create(new LiveWatchlistOptions { MaxSymbols = maxSymbols });
        return (new WatchlistService(db, _time, options), db);
    }

    [Theory]
    [InlineData("asts")]
    [InlineData("  asts  ")]
    [InlineData("AsTs")]
    public async Task AddItemAsync_normalises_symbol_to_trimmed_uppercase(string input)
    {
        var (service, db) = NewService();
        await using var _ = db;

        var item = await service.AddItemAsync(new CreateWatchlistItemRequest(input));

        Assert.Equal("ASTS", item.Symbol);
    }

    [Fact]
    public async Task AddItemAsync_rejects_a_duplicate_symbol()
    {
        var (service, db) = NewService();
        await using var _ = db;
        await service.AddItemAsync(new CreateWatchlistItemRequest("ASTS"));

        await Assert.ThrowsAsync<ConflictException>(
            () => service.AddItemAsync(new CreateWatchlistItemRequest("asts")));
    }

    [Fact]
    public async Task AddItemAsync_rejects_an_empty_symbol()
    {
        var (service, db) = NewService();
        await using var _ = db;

        await Assert.ThrowsAsync<ValidationException>(
            () => service.AddItemAsync(new CreateWatchlistItemRequest("   ")));
    }

    [Fact]
    public async Task AddItemAsync_rejects_the_symbol_past_the_cap()
    {
        var (service, db) = NewService(maxSymbols: 2);
        await using var _ = db;
        await service.AddItemAsync(new CreateWatchlistItemRequest("AAA"));
        await service.AddItemAsync(new CreateWatchlistItemRequest("BBB"));

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.AddItemAsync(new CreateWatchlistItemRequest("CCC")));

        // The message must name the cap: a blank row with no explanation is the failure
        // mode this check exists to prevent.
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public async Task UpdateItemAsync_leaves_omitted_fields_unchanged()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var item = await service.AddItemAsync(
            new CreateWatchlistItemRequest("ASTS", DisplayName: "AST SpaceMobile"));

        var updated = await service.UpdateItemAsync(item.Id, new UpdateWatchlistItemRequest());

        Assert.Equal("AST SpaceMobile", updated!.DisplayName);
    }

    [Fact]
    public async Task UpdateItemAsync_clears_display_name_on_empty_string()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var item = await service.AddItemAsync(
            new CreateWatchlistItemRequest("ASTS", DisplayName: "AST SpaceMobile"));

        var updated = await service.UpdateItemAsync(
            item.Id, new UpdateWatchlistItemRequest(DisplayName: ""));

        Assert.Null(updated!.DisplayName);
    }

    [Fact]
    public async Task UpdateItemAsync_moves_an_item_to_ungrouped_when_ClearGroup_is_set()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var group = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));
        var item = await service.AddItemAsync(
            new CreateWatchlistItemRequest("ASTS", GroupId: group.Id));

        var updated = await service.UpdateItemAsync(
            item.Id, new UpdateWatchlistItemRequest(ClearGroup: true));

        Assert.Null(updated!.GroupId);
    }

    [Fact]
    public async Task RemoveGroupAsync_orphans_its_items_rather_than_deleting_them()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var group = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));
        await service.AddItemAsync(new CreateWatchlistItemRequest("ASTS", GroupId: group.Id));

        await service.RemoveGroupAsync(group.Id);

        var items = await service.ListItemsAsync();
        var survivor = Assert.Single(items);
        Assert.Equal("ASTS", survivor.Symbol);
        Assert.Null(survivor.GroupId);
    }

    [Fact]
    public async Task AddGroupAsync_rejects_a_duplicate_name()
    {
        var (service, db) = NewService();
        await using var _ = db;
        await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));

        await Assert.ThrowsAsync<ConflictException>(
            () => service.AddGroupAsync(new CreateWatchlistGroupRequest("space")));
    }

    [Fact]
    public async Task ReorderAsync_applies_group_and_sort_order_together()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var group = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));
        var a = await service.AddItemAsync(new CreateWatchlistItemRequest("AAA"));
        var b = await service.AddItemAsync(new CreateWatchlistItemRequest("BBB"));

        await service.ReorderAsync(new ReorderRequest([
            new ReorderEntry(b.Id, group.Id, 0),
            new ReorderEntry(a.Id, group.Id, 1),
        ]));

        var items = await service.ListItemsAsync();
        Assert.Equal(["BBB", "AAA"], items.Select(i => i.Symbol));
        Assert.All(items, i => Assert.Equal(group.Id, i.GroupId));
    }

    [Fact]
    public async Task ListSymbolsAsync_returns_every_symbol_across_groups()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var group = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));
        await service.AddItemAsync(new CreateWatchlistItemRequest("AAA", GroupId: group.Id));
        await service.AddItemAsync(new CreateWatchlistItemRequest("BBB"));

        var symbols = await service.ListSymbolsAsync();

        Assert.Equal(["AAA", "BBB"], symbols.OrderBy(s => s));
    }
}
