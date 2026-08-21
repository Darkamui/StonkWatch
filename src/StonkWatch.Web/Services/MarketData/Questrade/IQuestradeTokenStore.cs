namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>
/// Persists the rotating Questrade refresh token. Behind an interface so the authenticator
/// can be tested without a database.
/// </summary>
public interface IQuestradeTokenStore
{
    /// <summary>The stored refresh token, or null if there is none or it cannot be decrypted.</summary>
    Task<string?> ReadAsync(CancellationToken ct = default);

    /// <summary>
    /// Upserts the single row. Must be durable before it returns — which holds because the
    /// statement autocommits, so never call this inside an outer transaction that could roll
    /// the rotation back after the access token has been used.
    /// </summary>
    /// <remarks>
    /// Takes no <see cref="CancellationToken"/> on purpose. By the time this is called the
    /// previous refresh token has already been consumed by Questrade, so cancelling the write
    /// does not abandon an operation — it destroys the only credential that still works. The
    /// missing parameter is the guard: there is no way for a caller to wire an abort token
    /// through and reopen that window.
    /// </remarks>
    Task SaveAsync(string refreshToken);

    /// <summary>
    /// Deletes the single row, if there is one. Called when Questrade rejects the stored token:
    /// it is dead, and while it remains stored it is preferred over
    /// <see cref="QuestradeOptions.BootstrapRefreshToken"/>, which is the value the user is
    /// told to set in order to recover. Clearing an empty store is not an error.
    /// </summary>
    Task ClearAsync(CancellationToken ct = default);
}
