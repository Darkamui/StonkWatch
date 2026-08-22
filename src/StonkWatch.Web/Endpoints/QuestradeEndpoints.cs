using StonkWatch.Web.Contracts;
using StonkWatch.Web.Services.MarketData.Questrade;

namespace StonkWatch.Web.Endpoints;

/// <summary>
/// Lets the user hand the app its first Questrade refresh token, and see whether the one it
/// has is still good. Mapped only when <c>Questrade:Enabled</c> is true — see
/// <c>Program.cs</c>, which is also what keeps these routes from existing at all otherwise.
/// </summary>
public static class QuestradeEndpoints
{
    public static void MapQuestradeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/questrade").RequireAuthorization("CookieOrApiKey");

        group.MapGet("/status", async (
            IQuestradeAuthenticator authenticator, CancellationToken ct) =>
        {
            try
            {
                // The session itself is never returned — connected is all the caller needs,
                // and the access token and api_server must never leave this process.
                await authenticator.GetSessionAsync(ct);
                return Results.Ok(new QuestradeStatusDto(true, null));
            }
            catch (QuestradeReauthorizationRequiredException ex)
            {
                // ex.Message is a fixed, actionable string that never contains a token —
                // see QuestradeAuthenticator, which is careful never to interpolate one in.
                return Results.Ok(new QuestradeStatusDto(false, ex.Message));
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                // Everything GetSessionAsync can throw that isn't "re-authorize": a Questrade
                // 5xx or unparseable body, a transport failure, or the 20s timeout expiring.
                // The contract here is "connected means the session call succeeded" — a
                // transient failure is connected:false, not a 500 from the one endpoint
                // operations.md tells a human to check when something looks wrong. The message
                // is fixed and names no provider detail, so it can never carry a token.
                return Results.Ok(new QuestradeStatusDto(
                    false, "Questrade could not be reached; the connection will be retried."));
            }
        });

        group.MapPost("/authorize", async (
            AuthorizeQuestradeRequest request,
            IQuestradeTokenStore store,
            IQuestradeAuthenticator authenticator,
            CancellationToken ct) =>
        {
            // Guards the blank cases (omitted, "", "   ", JSON null) before anything touches
            // the store or Questrade. Not a ValidationException from a service: there is no
            // service layer here, just the two Questrade abstractions this endpoint composes.
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                return Results.BadRequest(new { error = "A refresh token is required." });
            }

            // No CancellationToken on SaveAsync is deliberate (see IQuestradeTokenStore) —
            // nothing has been consumed from Questrade yet at this point, so there is nothing
            // this call could lose by running to completion.
            await store.SaveAsync(request.RefreshToken);

            // Drops any cached session so the call below is forced to actually exercise the
            // token just saved, rather than reusing one still live from a previous connection.
            authenticator.Invalidate();

            try
            {
                // Proves the token works: GetSessionAsync reads the store (finds what was
                // just saved), refreshes against Questrade, and — on success — persists the
                // rotated token, replacing whatever dead value was there before.
                await authenticator.GetSessionAsync(ct);
                return Results.Ok(new { connected = true });
            }
            catch (QuestradeReauthorizationRequiredException)
            {
                // Never the submitted token, in success or failure — only ever this fixed
                // message. QuestradeAuthenticator already cleared the just-saved token from
                // the store on the 400 that produced this, so a corrected retry isn't blocked
                // by it lingering there.
                return Results.BadRequest(
                    new { error = "Questrade rejected the submitted refresh token." });
            }
        });
    }
}
