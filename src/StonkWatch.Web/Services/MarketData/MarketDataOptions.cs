using System.ComponentModel.DataAnnotations;

namespace StonkWatch.Web.Services.MarketData;

public class MarketDataOptions
{
    public const string SectionName = "MarketData";

    [Required]
    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.twelvedata.com/";

    /// <summary>
    /// Symbols per request. Twelve Data accepts a comma-separated list, and batching is what
    /// keeps a 40-ticker watchlist inside the free tier's daily request budget.
    /// </summary>
    [Range(1, 120)]
    public int BatchSize { get; set; } = 20;

    public int TimeoutSeconds { get; set; } = 30;
}
