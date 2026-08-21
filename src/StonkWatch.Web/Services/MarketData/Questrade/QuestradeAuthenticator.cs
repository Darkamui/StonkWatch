using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>
/// A valid Questrade access token and the <c>api_server</c> base URL that goes with it.
/// <paramref name="ApiServer"/> always ends with <c>/</c> and is whatever the token response
/// returned — Questrade moves accounts between servers, so it is never hard-coded.
/// </summary>
public record QuestradeSession(string AccessToken, string ApiServer, DateTimeOffset ExpiresAt);

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

        var token = await ReadTokenResponseAsync(response);

        // Failure mode 1: the rotation must be durable before anything can use the access
        // token it came with. Awaited here, ahead of the cache write and the return.
        await SaveRefreshTokenAsync(token.RefreshToken);

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

    private static async Task<TokenResponse> ReadTokenResponseAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        var accessToken = ReadString(root, "access_token");
        var apiServer = ReadString(root, "api_server");
        var refreshToken = ReadString(root, "refresh_token");

        if (accessToken is null || apiServer is null || refreshToken is null
            || !root.TryGetProperty("expires_in", out var expiresIn)
            || !expiresIn.TryGetInt32(out var seconds))
        {
            // Names the missing shape, never a value.
            throw new InvalidOperationException(
                "The Questrade token response was missing access_token, api_server, "
                + "expires_in, or refresh_token.");
        }

        return new TokenResponse(accessToken, apiServer, seconds, refreshToken);
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

    private async Task SaveRefreshTokenAsync(string refreshToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<IQuestradeTokenStore>()
            .SaveAsync(refreshToken);
    }

    private record TokenResponse(
        string AccessToken, string ApiServer, int ExpiresIn, string RefreshToken);
}
