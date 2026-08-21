using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>
/// A valid Questrade access token and the <c>api_server</c> base URL that goes with it.
/// <paramref name="ApiServer"/> always ends with <c>/</c> and is whatever the token response
/// returned — Questrade moves accounts between servers, so it is never hard-coded.
/// </summary>
public record QuestradeSession(string AccessToken, string ApiServer, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Records synthesize a <c>ToString()</c> that prints every property, so a single
    /// <c>LogInformation("{Session}", session)</c> anywhere downstream would write a live
    /// bearer credential to disk. Everything that is not a credential still prints, because a
    /// formatted session that says nothing is one nobody will keep.
    /// </summary>
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append("AccessToken = [redacted], ");
        builder.Append("ApiServer = ").Append(ApiServer).Append(", ");
        builder.Append("ExpiresAt = ").Append(ExpiresAt.ToString("o", CultureInfo.InvariantCulture));
        return true;
    }
}

public interface IQuestradeAuthenticator
{
    /// <summary>Returns a session valid now, refreshing if needed. Single-flight.</summary>
    Task<QuestradeSession> GetSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops the cached session so the next GetSessionAsync refreshes.
    /// Call this when Questrade answers 401 to an API request.
    /// </summary>
    void Invalidate();
}

/// <summary>
/// The refresh token is gone or spent, and only the user can fix it by re-authorizing in the
/// Questrade portal. Distinct from a transient failure so the UI can say so.
/// </summary>
public class QuestradeReauthorizationRequiredException(string message) : Exception(message);

/// <summary>
/// Hands out Questrade access tokens, refreshing when needed.
/// </summary>
/// <remarks>
/// Questrade refresh tokens are single-use and rotating: every refresh consumes the token
/// presented and returns a new one, which is the only way back in. Three things would lose
/// it, and the shape of this class is dictated by all three:
/// <list type="number">
/// <item>A crash between "used the old token" and "stored the new one" — so the rotation is
/// persisted, and awaited, before the session is cached or returned.</item>
/// <item>Two concurrent refreshes, the second presenting an already-consumed token — so
/// refresh is single-flight behind a semaphore, with the cache re-checked after acquiring
/// it.</item>
/// <item>Idling past the three-day refresh-token expiry — unavoidable, so it surfaces as
/// <see cref="QuestradeReauthorizationRequiredException"/> rather than a generic failure.</item>
/// </list>
/// Singleton, so the scoped token store is resolved through a fresh scope per call; this
/// class never holds a DbContext.
/// </remarks>
public class QuestradeAuthenticator(
    HttpClient http,
    IServiceScopeFactory scopeFactory,
    IOptions<QuestradeOptions> options,
    TimeProvider timeProvider,
    ILogger<QuestradeAuthenticator> logger) : IQuestradeAuthenticator
{
    /// <summary>
    /// A token is never handed out in the last minute of its life, so a request that starts
    /// just before expiry cannot arrive just after it.
    /// </summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private volatile QuestradeSession? _session;

    public async Task<QuestradeSession> GetSessionAsync(CancellationToken ct = default)
    {
        if (TryGetLiveSession(out var cached))
        {
            return cached;
        }

        await _refreshGate.WaitAsync(ct);
        try
        {
            // Re-checked inside the gate: a caller that queued behind a refresh must take its
            // result, not start a second one against a token the first has already consumed.
            if (TryGetLiveSession(out cached))
            {
                return cached;
            }

            return await RefreshAsync();
        }
        finally
        {
            // Always released, including after a transient failure: the next call has to be
            // able to retry, and a poisoned gate would wedge the whole feed.
            _refreshGate.Release();
        }
    }

    public void Invalidate() =>
        // The cached session only. A 401 means the access token is stale, not that the
        // refresh token is bad, and clearing the store would force a manual re-authorization.
        _session = null;

    private bool TryGetLiveSession([NotNullWhen(true)] out QuestradeSession? session)
    {
        session = _session;
        return session is not null && timeProvider.GetUtcNow() < session.ExpiresAt;
    }

    /// <remarks>
    /// Deliberately takes no CancellationToken. Once the token exchange is under way,
    /// Questrade may already have consumed the refresh token, and abandoning the call before
    /// the replacement is stored would lose it. HttpClient.Timeout still bounds the wait.
    /// </remarks>
    private async Task<QuestradeSession> RefreshAsync()
    {
        var refreshToken = await ReadRefreshTokenAsync() ?? options.Value.BootstrapRefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new QuestradeReauthorizationRequiredException(
                "No Questrade refresh token is available. Re-authorize StonkWatch in the "
                + "Questrade portal and set Questrade:BootstrapRefreshToken.");
        }

        using var response = await SendRefreshRequestAsync(refreshToken);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Questrade's answer to a consumed or expired refresh token. The body can echo
            // the token, so it is never read or logged.
            _session = null;

            // The presented token is dead, and the request shape around it is constant and
            // generated by this class, so the token is the only variable that can produce a
            // 400. Dropping it is what makes the advice below true: ReadRefreshTokenAsync
            // prefers the stored token over the configured one, so leaving a dead value in the
            // store means the bootstrap token the user is told to set can never be reached,
            // and the only escape is a DELETE against the production database.
            await ClearStoredTokenAsync();

            logger.LogWarning(
                "Questrade rejected the refresh token; re-authorization is required.");
            throw new QuestradeReauthorizationRequiredException(
                "Questrade rejected the stored refresh token. Re-authorize StonkWatch in the "
                + "Questrade portal and set Questrade:BootstrapRefreshToken to the new token.");
        }

        if (!response.IsSuccessStatusCode)
        {
            // Transient: nothing is cached, nothing is cleared, and the gate is released by
            // the caller's finally, so the next call retries.
            logger.LogWarning(
                "Questrade token request failed with HTTP {StatusCode}.", (int)response.StatusCode);
            throw new InvalidOperationException(
                $"Questrade token request failed with HTTP {(int)response.StatusCode}.");
        }

        using var document = await ParseAsync(response);
        var root = document.RootElement;

        // Failure mode 1, and the whole reason for the ordering below: by the time this line
        // runs Questrade has already consumed the token that was presented, so the rotated one
        // in this body is the only credential left that works. It is rescued and made durable
        // before anything else is read, because anything that throws in between — a missing
        // field, an expires_in in an unexpected shape — would throw the account away with it.
        var rotated = ReadString(root, "refresh_token");
        if (rotated is null)
        {
            // Nothing to rescue and the presented token is spent: unrecoverable in exactly the
            // way an expired token is, so it arrives as the same actionable type.
            _session = null;
            logger.LogWarning(
                "Questrade returned a success response with no refresh token; "
                + "re-authorization is required.");
            throw new QuestradeReauthorizationRequiredException(
                "Questrade returned no refresh token, so the previous one is spent. "
                + "Re-authorize StonkWatch in the Questrade portal and set "
                + "Questrade:BootstrapRefreshToken to the new token.");
        }

        await PersistRotationAsync(rotated);

        var token = ReadTokenResponse(root, rotated);

        var session = new QuestradeSession(
            token.AccessToken,
            NormaliseApiServer(token.ApiServer),
            timeProvider.GetUtcNow() + TimeSpan.FromSeconds(token.ExpiresIn) - ExpiryMargin);

        _session = session;
        logger.LogInformation(
            "Questrade access token refreshed; usable until {ExpiresAt:o}.", session.ExpiresAt);
        return session;
    }

    private async Task<HttpResponseMessage> SendRefreshRequestAsync(string refreshToken)
    {
        // Form body, never the query string: a URL ends up in access logs and proxy traces.
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.LoginUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            })
        };

        return await http.SendAsync(request);
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();

        try
        {
            return await JsonDocument.ParseAsync(stream);
        }
        catch (JsonException)
        {
            // Unrecoverable — there is no refresh token to rescue from a body that will not
            // parse. Re-shaped so callers see the documented failure type; the JsonException
            // is deliberately not attached, because its message can quote the fragment it
            // choked on, and that fragment is a token response.
            throw new InvalidOperationException(
                "The Questrade token response was not valid JSON.");
        }
    }

    /// <summary>
    /// Reads everything except the refresh token, which the caller has already rescued and
    /// persisted — hence <paramref name="refreshToken"/> arriving as an argument.
    /// </summary>
    private static TokenResponse ReadTokenResponse(JsonElement root, string refreshToken)
    {
        var accessToken = ReadString(root, "access_token");
        var apiServer = ReadString(root, "api_server");
        var seconds = ReadExpiresIn(root);

        if (accessToken is null || apiServer is null || seconds is null)
        {
            // Names the missing shape, never a value.
            throw new InvalidOperationException(
                "The Questrade token response was missing access_token, api_server, "
                + "or expires_in.");
        }

        return new TokenResponse(accessToken, apiServer, seconds.Value, refreshToken);
    }

    /// <summary>
    /// Accepts <c>expires_in</c> as a JSON number or a JSON string. Plenty of OAuth servers
    /// send the string form, and rejecting it would cost the user their connection over a
    /// formatting detail.
    /// </summary>
    private static int? ReadExpiresIn(JsonElement root)
    {
        if (!root.TryGetProperty("expires_in", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>Callers append <c>v1/…</c> to this, so the trailing slash has to be there.</summary>
    private static string NormaliseApiServer(string apiServer) =>
        apiServer.EndsWith('/') ? apiServer : apiServer + "/";

    private async Task<string?> ReadRefreshTokenAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IQuestradeTokenStore>()
            .ReadAsync();
    }

    /// <summary>
    /// Drops the dead stored token so the configured bootstrap token becomes reachable.
    /// Failing to clear leaves the user exactly where they were — locked out with no way back
    /// except manual database surgery — but "re-authorize" is still the actionable answer, so
    /// that exception is what propagates and this failure is named in the log instead.
    /// </summary>
    private async Task ClearStoredTokenAsync()
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider
                .GetRequiredService<IQuestradeTokenStore>()
                .ClearAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Questrade rejected the stored refresh token, but it could not be cleared from "
                + "the database. Until it is removed it will be preferred over "
                + "Questrade:BootstrapRefreshToken, so setting that value will not restore "
                + "access on its own.");
        }
    }

    /// <summary>
    /// Stores the rotated token, and makes a noise if it cannot. This is the worst moment in
    /// the class: the presented token is already spent and its replacement now exists nowhere,
    /// so the connection is dead and only the user can revive it. The exception propagates —
    /// nothing is cached, so no caller receives a session built on a rotation that was lost —
    /// but on its own it would surface half an hour later as a bare "invalid_grant", pointing
    /// at the wrong cause. The log entry is what connects the two.
    /// </summary>
    private async Task PersistRotationAsync(string refreshToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider
                .GetRequiredService<IQuestradeTokenStore>()
                .SaveAsync(refreshToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "The rotated Questrade refresh token could not be persisted. The previous "
                + "token is already spent, so Questrade access is lost until you re-authorize "
                + "StonkWatch in the Questrade portal.");
            throw;
        }
    }

    /// <summary>
    /// Internal rather than private only so the redaction below can be pinned by a test of its
    /// own; nothing outside this class constructs one.
    /// </summary>
    internal record TokenResponse(
        string AccessToken, string ApiServer, int ExpiresIn, string RefreshToken)
    {
        /// <summary>Same reasoning as <see cref="QuestradeSession"/>: two credentials here.</summary>
        protected virtual bool PrintMembers(StringBuilder builder)
        {
            builder.Append("AccessToken = [redacted], ");
            builder.Append("ApiServer = ").Append(ApiServer).Append(", ");
            builder.Append("ExpiresIn = ").Append(ExpiresIn.ToString(CultureInfo.InvariantCulture));
            builder.Append(", RefreshToken = [redacted]");
            return true;
        }
    }
}
