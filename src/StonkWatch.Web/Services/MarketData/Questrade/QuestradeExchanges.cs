namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>
/// The listing venues StonkWatch will price. Shared by <see cref="QuestradeSymbolResolver"/>
/// and <see cref="QuestradeSymbolSearch"/> on purpose, because the two answer halves of the
/// same question: search decides what the user is allowed to add, the resolver decides what
/// the poller can turn into a quote. Were they to drift apart, search would offer a listing
/// the resolver then refuses, and the row would sit at an em dash forever with nothing
/// anywhere saying why.
/// </summary>
internal static class QuestradeExchanges
{
    /// <summary>
    /// US equities and ETFs only — a same-ticker TSX listing would otherwise supply Canadian
    /// prices without anything downstream noticing.
    /// </summary>
    public static readonly IReadOnlySet<string> Us =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NASDAQ", "NYSE", "AMEX", "ARCA", "BATS", "NYSEMKT"
        };
}
