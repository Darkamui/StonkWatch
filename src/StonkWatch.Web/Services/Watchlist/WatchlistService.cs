using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Data.Entities;

namespace StonkWatch.Web.Services.Watchlist;

public class WatchlistService(
    StonkWatchDbContext db,
    TimeProvider timeProvider,
    IOptions<LiveWatchlistOptions> options)
{
    private readonly LiveWatchlistOptions _options = options.Value;

    // ---------- Reads ----------

    public async Task<List<WatchlistGroupDto>> ListGroupsAsync(CancellationToken ct = default) =>
        (await db.WatchlistGroups.AsNoTracking()
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<List<WatchlistItemDto>> ListItemsAsync(CancellationToken ct = default) =>
        (await db.WatchlistItems.AsNoTracking()
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Symbol).ToListAsync(ct))
        .Select(ToDto).ToList();

    public Task<List<string>> ListSymbolsAsync(CancellationToken ct = default) =>
        db.WatchlistItems.AsNoTracking().Select(i => i.Symbol).ToListAsync(ct);

    // ---------- Items ----------

    public async Task<WatchlistItemDto> AddItemAsync(
        CreateWatchlistItemRequest request, CancellationToken ct = default)
    {
        var symbol = Normalize(request.Symbol ?? "");
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ValidationException("Symbol is required.");
        }

        if (await db.WatchlistItems.AnyAsync(i => i.Symbol == symbol, ct))
        {
            throw new ConflictException($"'{symbol}' is already on the watchlist.");
        }

        var count = await db.WatchlistItems.CountAsync(ct);
        if (count >= _options.MaxSymbols)
        {
            throw new ValidationException(
                $"The watchlist is limited to {_options.MaxSymbols} symbols "
                + "by the market data provider's streaming cap. Remove one first.");
        }

        if (request.GroupId is { } groupId
            && !await db.WatchlistGroups.AnyAsync(g => g.Id == groupId, ct))
        {
            throw new ValidationException($"Group '{groupId}' does not exist.");
        }

        var nextOrder = await db.WatchlistItems
            .Where(i => i.GroupId == request.GroupId)
            .Select(i => (int?)i.SortOrder)
            .MaxAsync(ct) ?? -1;

        var item = new WatchlistItem
        {
            Id = Guid.NewGuid(),
            GroupId = request.GroupId,
            Symbol = symbol,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName,
            SortOrder = nextOrder + 1,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        db.WatchlistItems.Add(item);
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task<WatchlistItemDto?> UpdateItemAsync(
        Guid id, UpdateWatchlistItemRequest request, CancellationToken ct = default)
    {
        var item = await db.WatchlistItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null)
        {
            return null;
        }

        item.DisplayName = MergeString(request.DisplayName, item.DisplayName);

        if (request.ClearGroup)
        {
            item.GroupId = null;
        }
        else if (request.GroupId is { } groupId)
        {
            if (!await db.WatchlistGroups.AnyAsync(g => g.Id == groupId, ct))
            {
                throw new ValidationException($"Group '{groupId}' does not exist.");
            }
            item.GroupId = groupId;
        }

        if (request.SortOrder is { } sortOrder)
        {
            item.SortOrder = sortOrder;
        }

        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task<bool> RemoveItemAsync(Guid id, CancellationToken ct = default)
    {
        var item = await db.WatchlistItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null)
        {
            return false;
        }

        db.WatchlistItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---------- Groups ----------

    public async Task<WatchlistGroupDto> AddGroupAsync(
        CreateWatchlistGroupRequest request, CancellationToken ct = default)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new ValidationException("Group name is required.");
        }

        if (await db.WatchlistGroups.AnyAsync(g => g.Name.ToUpper() == name.ToUpperInvariant(), ct))
        {
            throw new ConflictException($"A group named '{name}' already exists.");
        }

        var nextOrder = await db.WatchlistGroups.Select(g => (int?)g.SortOrder).MaxAsync(ct) ?? -1;

        var group = new WatchlistGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            SortOrder = nextOrder + 1,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        db.WatchlistGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return ToDto(group);
    }

    public async Task<WatchlistGroupDto?> UpdateGroupAsync(
        Guid id, UpdateWatchlistGroupRequest request, CancellationToken ct = default)
    {
        var group = await db.WatchlistGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            var name = request.Name.Trim();
            if (await db.WatchlistGroups.AnyAsync(
                    g => g.Id != id && g.Name.ToUpper() == name.ToUpperInvariant(), ct))
            {
                throw new ConflictException($"A group named '{name}' already exists.");
            }
            group.Name = name;
        }

        if (request.SortOrder is { } sortOrder)
        {
            group.SortOrder = sortOrder;
        }

        await db.SaveChangesAsync(ct);
        return ToDto(group);
    }

    /// <summary>
    /// Deletes the group and leaves its symbols on the watchlist, ungrouped. The FK is
    /// configured ON DELETE SET NULL, but tracked entities are updated explicitly so the
    /// in-memory graph matches the database without a reload.
    /// </summary>
    public async Task<bool> RemoveGroupAsync(Guid id, CancellationToken ct = default)
    {
        var group = await db.WatchlistGroups
            .Include(g => g.Items)
            .FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
        {
            return false;
        }

        foreach (var item in group.Items)
        {
            item.GroupId = null;
        }

        db.WatchlistGroups.Remove(group);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---------- Reorder ----------

    public async Task ReorderAsync(ReorderRequest request, CancellationToken ct = default)
    {
        if (request.Items is null)
        {
            throw new ValidationException("Items is required.");
        }

        var ids = request.Items.Select(e => e.Id).ToHashSet();
        var items = await db.WatchlistItems.Where(i => ids.Contains(i.Id)).ToListAsync(ct);
        var byId = items.ToDictionary(i => i.Id);

        var validGroups = await db.WatchlistGroups.Select(g => g.Id).ToListAsync(ct);

        foreach (var entry in request.Items)
        {
            if (!byId.TryGetValue(entry.Id, out var item))
            {
                throw new ValidationException($"Watchlist item '{entry.Id}' does not exist.");
            }

            if (entry.GroupId is { } groupId && !validGroups.Contains(groupId))
            {
                throw new ValidationException($"Group '{groupId}' does not exist.");
            }

            item.GroupId = entry.GroupId;
            item.SortOrder = entry.SortOrder;
        }

        await db.SaveChangesAsync(ct);
    }

    // ---------- Mapping ----------

    private static string Normalize(string symbol) => symbol.Trim().ToUpperInvariant();

    private static string? MergeString(string? incoming, string? current) => incoming switch
    {
        null => current,
        "" => null,
        _ => incoming
    };

    private static WatchlistGroupDto ToDto(WatchlistGroup g) => new(g.Id, g.Name, g.SortOrder);

    private static WatchlistItemDto ToDto(WatchlistItem i) =>
        new(i.Id, i.GroupId, i.Symbol, i.DisplayName, i.SortOrder);
}
