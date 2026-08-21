namespace StonkWatch.Web.Data.Entities;

/// <summary>
/// Single-row table: the app authenticates as exactly one Questrade account.
/// The refresh token is stored encrypted; see QuestradeTokenStore.
/// </summary>
/// <remarks>
/// Questrade refresh tokens are single-use and rotating — every refresh consumes the stored
/// token and returns a new one, and the new one is the only way back in without the user
/// re-authorizing by hand. That is why this value cannot live in configuration like every
/// other secret in the app, and why the check constraint forbids a second row: two competing
/// tokens would mean one of them is already consumed.
/// </remarks>
public class QuestradeToken
{
    public int Id { get; set; }
    public string ProtectedRefreshToken { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
