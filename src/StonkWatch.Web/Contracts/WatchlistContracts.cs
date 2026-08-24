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

/// <summary>One rendered row: the stored item joined to whatever the cache currently knows.</summary>
/// <param name="ExtendedChangePercent">
/// The extended-hours move as a percentage, not a price. A second price column repeated
/// <paramref name="Last"/> outside regular hours — <c>Last</c> already *is* the extended print
/// then — which told the reader nothing. Null during the regular session and whenever there is
/// no extended print to measure.
/// </param>
public record WatchlistRowDto(
    Guid Id,
    Guid? GroupId,
    string Symbol,
    string Label,
    int SortOrder,
    decimal? Last,
    decimal? ChangePercent,
    long? Volume,
    decimal? ExtendedChangePercent,
    DateTimeOffset? LastAt);

/// <summary>
/// One candidate from the sidebar's symbol search. <c>SymbolId</c> is Questrade's own public
/// identifier for the listing, carried so the caller can tell two same-ticker listings apart;
/// it is not a secret and not a StonkWatch key.
/// </summary>
public record SymbolSearchResultDto(
    string Symbol, string Description, string Exchange, int SymbolId);

/// <summary>
/// Which part of the trading day the server believes it is in, pushed on the SSE stream so the
/// sidebar's status line can say why prices are not moving. A string rather than the enum so
/// adding a phase later cannot break a browser holding an old script.
/// </summary>
public record MarketPhaseDto(string Phase);

public record WatchlistViewDto(
    IReadOnlyList<WatchlistGroupDto> Groups,
    IReadOnlyList<WatchlistRowDto> Rows,
    DateTimeOffset ServerTime);
