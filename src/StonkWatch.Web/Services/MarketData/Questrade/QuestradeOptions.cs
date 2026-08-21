namespace StonkWatch.Web.Services.MarketData.Questrade;

public class QuestradeOptions
{
    public const string SectionName = "Questrade";

    public bool Enabled { get; set; }

    public string LoginUrl { get; set; } = "https://login.questrade.com/oauth2/token";

    /// <summary>
    /// One-time seed, obtained by hand from the Questrade portal. Rotations are persisted
    /// to the database; this value is only consulted when the database has no token yet.
    /// </summary>
    public string BootstrapRefreshToken { get; set; } = "";
}
