using System.Net;
using System.Net.Http.Headers;

namespace StonkWatch.Web.Services.MarketData.Questrade;

/// <summary>
/// The one 401 policy shared by every bearer-authenticated Questrade REST call
/// (<see cref="QuestradeQuoteClient"/> and <see cref="QuestradeSymbolResolver"/>): fetch a
/// session, send; on 401 invalidate the cached session and retry once; a second 401 throws so
/// the retry can never loop. Any other non-success status is logged (the path only — the token
/// travels in the header and must never reach a URL or a log line) and answered with
/// <see langword="null"/>, which callers turn into "nothing for this tick" rather than letting
/// one failed poll take the worker down.
/// </summary>
/// <remarks>
/// Before this existed, the resolver duplicated the quote client's request-building without its
/// retry policy, and the divergence let a stale access token blacklist a ticker for 30 minutes
/// instead of triggering the same recovery a stale-token quote request gets. One helper, one
/// policy.
/// </remarks>
internal static class QuestradeHttp
{
    public static async Task<HttpResponseMessage?> SendWithRetryAsync(
        HttpClient http,
        IQuestradeAuthenticator authenticator,
        ILogger logger,
        string path,
        Func<QuestradeSession, string> buildUrl,
        CancellationToken ct)
    {
        var session = await authenticator.GetSessionAsync(ct);
        var response = await SendAsync(http, session, buildUrl, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            authenticator.Invalidate();

            session = await authenticator.GetSessionAsync(ct);
            response = await SendAsync(http, session, buildUrl, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new HttpRequestException(
                    $"Questrade rejected the access token twice for {path}.",
                    inner: null, HttpStatusCode.Unauthorized);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Questrade request to {Path} failed with {StatusCode}", path, (int)response.StatusCode);
            response.Dispose();
            return null;
        }

        return response;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, QuestradeSession session, Func<QuestradeSession, string> buildUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, buildUrl(session));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        return await http.SendAsync(request, ct);
    }
}
