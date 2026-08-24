namespace StonkWatch.Web.Services.MarketData;

public record Quote(
    string Symbol,
    decimal Price,
    DateTimeOffset At,
    long? Volume = null,
    decimal? PreviousClose = null,
    decimal? ExtendedPrice = null,
    DateTimeOffset? ExtendedAt = null,
    decimal? RegularClose = null);

public interface IQuoteProvider
{
    /// <summary>
    /// Fetches the latest price for each symbol. Symbols the provider cannot resolve are
    /// omitted from the result rather than throwing — one bad ticker must not lose a whole
    /// check cycle. Keys are the normalised (trimmed, uppercase) symbol.
    /// </summary>
    Task<IReadOnlyDictionary<string, Quote>> GetQuotesAsync(
        IReadOnlyCollection<string> symbols, CancellationToken ct = default);
}
