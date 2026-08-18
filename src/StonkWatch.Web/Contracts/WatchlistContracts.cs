namespace StonkWatch.Web.Contracts;

public record WatchlistGroupDto(Guid Id, string Name, int SortOrder);

public record WatchlistItemDto(
    Guid Id, Guid? GroupId, string Symbol, string? DisplayName, int SortOrder);

public record CreateWatchlistItemRequest(
    string Symbol, Guid? GroupId = null, string? DisplayName = null);

/// <summary>
/// PATCH semantics: omitted leaves a field unchanged, "" clears a string.
/// <paramref name="ClearGroup"/> exists because a nullable Guid cannot express "clear"
/// the way an empty string can.
/// </summary>
public record UpdateWatchlistItemRequest(
    string? DisplayName = null,
    Guid? GroupId = null,
    bool ClearGroup = false,
    int? SortOrder = null);

public record CreateWatchlistGroupRequest(string Name);

public record UpdateWatchlistGroupRequest(string? Name = null, int? SortOrder = null);

public record ReorderEntry(Guid Id, Guid? GroupId, int SortOrder);

public record ReorderRequest(IReadOnlyList<ReorderEntry> Items);
