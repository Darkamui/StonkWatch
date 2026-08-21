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

    [Fact]
    public async Task ListSymbolsAsync_orders_by_sort_order_like_ListItemsAsync()
    {
        // LiveWatchlistPollJob.MaxSymbols takes the first N of whatever this returns, so an
        // arbitrary storage order would make "the first N" undefined and let the polled set
        // change between ticks. Ordering must match ListItemsAsync's own SortOrder-then-Symbol.
        var (service, db) = NewService();
        await using var _ = db;
        var group = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));
        var a = await service.AddItemAsync(new CreateWatchlistItemRequest("AAA"));
        var b = await service.AddItemAsync(new CreateWatchlistItemRequest("BBB"));

        await service.ReorderAsync(new ReorderRequest([
            new ReorderEntry(b.Id, group.Id, 0),
            new ReorderEntry(a.Id, group.Id, 1),
        ]));

        var symbols = await service.ListSymbolsAsync();

        Assert.Equal(["BBB", "AAA"], symbols);
    }

    [Fact]
    public async Task UpdateGroupAsync_renames_the_group()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var group = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));

        var updated = await service.UpdateGroupAsync(
            group.Id, new UpdateWatchlistGroupRequest(Name: "AEROSPACE"));

        Assert.Equal("AEROSPACE", updated!.Name);
        var groups = await service.ListGroupsAsync();
        Assert.Equal("AEROSPACE", Assert.Single(groups).Name);
    }

    [Fact]
    public async Task UpdateGroupAsync_rejects_a_rename_to_a_name_another_group_already_holds()
    {
        var (service, db) = NewService();
        await using var _ = db;
        await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));
        var pharma = await service.AddGroupAsync(new CreateWatchlistGroupRequest("PHARMA"));

        await Assert.ThrowsAsync<ConflictException>(
            () => service.UpdateGroupAsync(pharma.Id, new UpdateWatchlistGroupRequest(Name: "space")));
    }

    [Fact]
    public async Task UpdateGroupAsync_applies_a_sort_order_change()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var group = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));

        var updated = await service.UpdateGroupAsync(
            group.Id, new UpdateWatchlistGroupRequest(SortOrder: 7));

        Assert.Equal(7, updated!.SortOrder);
    }

    [Fact]
    public async Task UpdateGroupAsync_returns_null_for_an_unknown_id()
    {
        var (service, db) = NewService();
        await using var _ = db;

        var updated = await service.UpdateGroupAsync(
            Guid.NewGuid(), new UpdateWatchlistGroupRequest(Name: "SPACE"));

        Assert.Null(updated);
    }

    [Fact]
    public async Task RemoveGroupAsync_returns_false_for_an_unknown_id()
    {
        var (service, db) = NewService();
        await using var _ = db;

        var removed = await service.RemoveGroupAsync(Guid.NewGuid());

        Assert.False(removed);
    }

    [Fact]
    public async Task RemoveItemAsync_removes_the_item_and_returns_true()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var item = await service.AddItemAsync(new CreateWatchlistItemRequest("ASTS"));

        var removed = await service.RemoveItemAsync(item.Id);

        Assert.True(removed);
        Assert.Empty(await service.ListItemsAsync());
    }

    [Fact]
    public async Task RemoveItemAsync_returns_false_for_an_unknown_id_and_removes_nothing()
    {
        var (service, db) = NewService();
        await using var _ = db;
        await service.AddItemAsync(new CreateWatchlistItemRequest("ASTS"));

        var removed = await service.RemoveItemAsync(Guid.NewGuid());

        Assert.False(removed);
        Assert.Single(await service.ListItemsAsync());
    }

    [Fact]
    public async Task ListGroupsAsync_returns_groups_ordered_by_sort_order_then_name()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var space = await service.AddGroupAsync(new CreateWatchlistGroupRequest("SPACE"));
        var pharma = await service.AddGroupAsync(new CreateWatchlistGroupRequest("PHARMA"));
        await service.UpdateGroupAsync(space.Id, new UpdateWatchlistGroupRequest(SortOrder: 0));
        await service.UpdateGroupAsync(pharma.Id, new UpdateWatchlistGroupRequest(SortOrder: 0));

        var groups = await service.ListGroupsAsync();

        Assert.Equal(["PHARMA", "SPACE"], groups.Select(g => g.Name));
        Assert.All(groups, g => Assert.Equal(0, g.SortOrder));
        Assert.Contains(groups, g => g.Id == space.Id);
        Assert.Contains(groups, g => g.Id == pharma.Id);
    }

    [Fact]
    public async Task AddItemAsync_rejects_a_GroupId_that_does_not_exist()
    {
        var (service, db) = NewService();
        await using var _ = db;

        await Assert.ThrowsAsync<ValidationException>(() => service.AddItemAsync(
            new CreateWatchlistItemRequest("ASTS", GroupId: Guid.NewGuid())));
    }

    [Fact]
    public async Task UpdateItemAsync_rejects_a_GroupId_that_does_not_exist()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var item = await service.AddItemAsync(new CreateWatchlistItemRequest("ASTS"));

        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateItemAsync(
            item.Id, new UpdateWatchlistItemRequest(GroupId: Guid.NewGuid())));
    }

    [Fact]
    public async Task ReorderAsync_rejects_an_unknown_item_id()
    {
        var (service, db) = NewService();
        await using var _ = db;

        await Assert.ThrowsAsync<ValidationException>(() => service.ReorderAsync(
            new ReorderRequest([new ReorderEntry(Guid.NewGuid(), null, 0)])));
    }

    [Fact]
    public async Task ReorderAsync_rejects_an_unknown_group_id()
    {
        var (service, db) = NewService();
        await using var _ = db;
        var item = await service.AddItemAsync(new CreateWatchlistItemRequest("ASTS"));

        await Assert.ThrowsAsync<ValidationException>(() => service.ReorderAsync(
            new ReorderRequest([new ReorderEntry(item.Id, Guid.NewGuid(), 0)])));
    }
}
