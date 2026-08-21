namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>
/// Persists the rotating Questrade refresh token. Behind an interface so the authenticator
/// can be tested without a database.
/// </summary>
public interface IQuestradeTokenStore
{
    /// <summary>The stored refresh token, or null if there is none or it cannot be decrypted.</summary>
    Task<string?> ReadAsync(CancellationToken ct = default);

    /// <summary>Upserts the single row. Must be durable before it returns.</summary>
    Task SaveAsync(string refreshToken, CancellationToken ct = default);
}
