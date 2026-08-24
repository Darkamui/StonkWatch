namespace StonkWatch.Web.Data.Entities;

public class Candidate
{
    public Guid Id { get; set; }

    public required string Ticker { get; set; }
    public string? Company { get; set; }
    public string? Exchange { get; set; }
    public string? Currency { get; set; }

    public Priority Priority { get; set; } = Priority.Medium;
    public CandidateStatus Status { get; set; } = CandidateStatus.Idea;
    public Conviction? Conviction { get; set; }

    public string? PreferredSetup { get; set; }
    public string? Thesis { get; set; }

    public decimal? CurrentPrice { get; set; }
    public decimal? ReviewedPrice { get; set; }
    public DateTimeOffset? LastReviewed { get; set; }

    /// <summary>
    /// Last price seen by the price-check worker, kept separate from <see cref="CurrentPrice"/>
    /// (which a review writes) so the two never overwrite each other. Also serves as the
    /// "previous price" the level evaluator compares against on the next tick.
    /// </summary>
    public decimal? LastQuote { get; set; }
    public DateTimeOffset? QuoteAt { get; set; }

    public decimal? SupportLow { get; set; }
    public decimal? SupportHigh { get; set; }
    public decimal? SecondarySupportLow { get; set; }
    public decimal? SecondarySupportHigh { get; set; }
    public decimal? ReclaimTrigger1 { get; set; }
    public decimal? ReclaimTrigger2 { get; set; }
    public decimal? Invalidation { get; set; }
    public decimal? T1 { get; set; }
    public decimal? T2 { get; set; }

    public string? NextEvent { get; set; }
    public DateOnly? EventDate { get; set; }

    public DataQuality DataQuality { get; set; } = DataQuality.Unavailable;
    public string? MainRisk { get; set; }
    public string? SourceNotes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Alert> Alerts { get; set; } = [];
    public List<ReviewLogEntry> ReviewLogs { get; set; } = [];
    public List<CandidateHistoryEntry> HistoryEntries { get; set; } = [];
}
