using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using StonkWatch.Web.Data;

namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>
/// Stores the rotating Questrade refresh token in the single-row <c>questrade_token</c>
/// table, encrypted with ASP.NET Core Data Protection.
/// </summary>
/// <remarks>
/// Nothing here logs, throws, or returns the token value. The only secret that ever leaves
/// this class is the one the caller asked for, by return value.
/// </remarks>
public class QuestradeTokenStore(
    StonkWatchDbContext db,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider,
    ILogger<QuestradeTokenStore> logger) : IQuestradeTokenStore
{
    /// <summary>
    /// Verbatim and permanent: changing this purpose string orphans the stored token and
    /// forces the user to re-authorize by hand.
    /// </summary>
    private const string ProtectorPurpose = "StonkWatch.Questrade.RefreshToken";

    /// <summary>The only id the table's check constraint permits.</summary>
    private const int SingletonId = 1;

    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector(ProtectorPurpose);

    public async Task<string?> ReadAsync(CancellationToken ct = default)
    {
        var row = await db.QuestradeTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == SingletonId, ct);

        if (row is null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(row.ProtectedRefreshToken);
        }
        catch (CryptographicException)
        {
            // Almost always means the Data Protection key ring was regenerated — which is
            // what happens when DataProtectionKeysPath is not configured and the container
            // restarts. Treat it as "no token" so the bootstrap value can take over; the
            // exception carries ciphertext detail, so it is deliberately not logged.
            logger.LogWarning(
                "The stored Questrade refresh token could not be decrypted and was ignored. "
                + "This usually means the Data Protection keys were regenerated; "
                + "re-authorize Questrade and configure DataProtectionKeysPath.");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var protectedToken = _protector.Protect(refreshToken);

        // Npgsql only accepts UTC-normalized DateTimeOffset values for timestamptz columns.
        var now = timeProvider.GetUtcNow().ToUniversalTime();

        // A raw upsert rather than find-then-add: EF's read-modify-write would throw a
        // primary key violation if two saves ever raced, and a save that throws is a lost
        // refresh token, which locks the user out until they re-authorize by hand.
        //
        // Across two processes this is last-writer-wins, and that is deliberate. The check
        // constraint makes two competing rows impossible; it cannot make two competing
        // *values* impossible, and no in-process lock can either. Two instances refreshing one
        // Questrade account would each consume a token and invalidate the other's no matter
        // what this method did — the invariant is "never run two instances against one
        // account", enforced by deployment and documented in operations, not by a lock here.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO questrade_token (id, protected_refresh_token, updated_at)
             VALUES ({SingletonId}, {protectedToken}, {now})
             ON CONFLICT (id) DO UPDATE
                 SET protected_refresh_token = EXCLUDED.protected_refresh_token,
                     updated_at = EXCLUDED.updated_at
             """);
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken ct = default) =>
        // DELETE, not "set to empty": ReadAsync treats a missing row and an unusable one the
        // same way, but an empty string would have to be special-cased by every future reader.
        // No row to delete is a no-op, which is what the 400 path needs.
        db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM questrade_token WHERE id = {SingletonId}", ct);
}
