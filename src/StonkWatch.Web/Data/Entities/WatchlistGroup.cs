namespace StonkWatch.Web.Data.Entities;

/// <summary>
/// A named, collapsible section of the live watchlist ("SPACE", "PHARMA"). Purely an
/// organisational device — it carries no trading meaning and is unrelated to Candidate.
/// </summary>
public class WatchlistGroup
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<WatchlistItem> Items { get; set; } = [];
}
