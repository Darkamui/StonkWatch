using System.ComponentModel.DataAnnotations;

namespace StonkWatch.Web.Services.MarketData;

public class FinnhubOptions
{
    public const string SectionName = "MarketData:Finnhub";

    [Required]
    public string ApiKey { get; set; } = "";

    public string WebSocketUrl { get; set; } = "wss://ws.finnhub.io";

    public string BaseUrl { get; set; } = "https://finnhub.io/api/v1/";
}
