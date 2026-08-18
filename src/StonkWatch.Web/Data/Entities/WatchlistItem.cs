namespace StonkWatch.Web.Data.Entities;

/// <summary>
/// One symbol on the live watchlist. Deliberately independent of <see cref="Candidate"/>:
/// the watchlist is for watching, and nothing here reads or writes Candidate.LastQuote,
/// which the Tier 1 price-check worker owns.
/// </summary>
public class WatchlistItem
{
    public Guid Id { get; set; }

    /// <summary>Null means ungrouped; those rows render above the named groups.</summary>
    public Guid? GroupId { get; set; }
    public WatchlistGroup? Group { get; set; }

    /// <summary>Normalised on write: trimmed and uppercased.</summary>
    public required string Symbol { get; set; }

    /// <summary>Optional row label override. Falls back to the symbol.</summary>
    public string? DisplayName { get; set; }

    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
