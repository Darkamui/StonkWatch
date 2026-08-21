using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StonkWatch.Web.Services.MarketData.Questrade;

namespace StonkWatch.Web.Tests;

/// <summary>
/// Questrade refresh tokens are single-use: every refresh consumes the old one and returns a
/// new one, and losing the new one locks the user out until they re-authorize by hand in the
/// Questrade portal. These tests pin the three ways that can happen — a crash between "used"
/// and "stored", two concurrent refreshes, and an expired token — plus the rule that no token
/// value ever reaches a log or an exception message.
/// </summary>
public class QuestradeAuthenticatorTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 19, 13, 30, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);

    /// <summary>Everything <see cref="NewAuthenticator"/> builds, torn down with the test.</summary>
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    // ---- test doubles -------------------------------------------------------------------

    /// <summary>Stands in for the Questrade token endpoint, recording every request body.</summary>
    private sealed class TokenEndpointHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly Lock _sync = new();
        private int _count;

        public List<string> Bodies { get; } = [];
        public List<Uri> Uris { get; } = [];

        /// <summary>When set, every response waits on this before completing.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public int Count => Volatile.Read(ref _count);

        public static TokenEndpointHandler Always(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(_ => Respond(body, status));

        public static HttpResponseMessage Respond(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _count);
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (_sync)
            {
                Uris.Add(request.RequestUri!);
                Bodies.Add(body);
            }

            if (Gate is not null)
            {
                await Gate.Task;
            }

            return respond(n);
        }
    }

    /// <summary>Records save order and can be made to fail, without a database.</summary>
    private sealed class RecordingTokenStore(List<string>? events = null) : IQuestradeTokenStore
    {
        public string? Token { get; set; }
        public List<string> Saved { get; } = [];
        public int ReadCount { get; private set; }
        public Exception? ThrowOnSave { get; set; }

        public Task<string?> ReadAsync(CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(Token);
        }

        public Task SaveAsync(string refreshToken, CancellationToken ct = default)
        {
            if (ThrowOnSave is not null)
            {
                throw ThrowOnSave;
            }

            Token = refreshToken;
            Saved.Add(refreshToken);
            events?.Add("saved");
            return Task.CompletedTask;
        }
    }

    // ---- helpers ------------------------------------------------------------------------

    private static string TokenJson(
        string accessToken = "access-1",
        string apiServer = "https://api01.iq.questrade.com/",
        int expiresIn = 1800,
        string refreshToken = "rotated-1") =>
        $$"""
          {"access_token":"{{accessToken}}","api_server":"{{apiServer}}",
           "expires_in":{{expiresIn}},"refresh_token":"{{refreshToken}}","token_type":"Bearer"}
          """;

    private QuestradeAuthenticator NewAuthenticator(
        HttpMessageHandler handler,
        IQuestradeTokenStore store,
        string bootstrapRefreshToken = "bootstrap-token",
        ILogger<QuestradeAuthenticator>? logger = null,
        string loginUrl = "https://login.test.questrade.invalid/oauth2/token")
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // Disposing the client disposes the handler with it.
        var client = new HttpClient(handler);
        _disposables.Add(client);

        var options = Options.Create(new QuestradeOptions
        {
            Enabled = true,
            LoginUrl = loginUrl,
            BootstrapRefreshToken = bootstrapRefreshToken
        });

        return new QuestradeAuthenticator(
            client, scopeFactory, options, _time,
            logger ?? NullLogger<QuestradeAuthenticator>.Instance);
    }

    // ---- tests --------------------------------------------------------------------------

    [Fact]
    public async Task The_first_refresh_uses_the_bootstrap_token_when_the_store_is_empty()
    {
        var handler = TokenEndpointHandler.Always(TokenJson());
        var store = new RecordingTokenStore();
        var authenticator = NewAuthenticator(handler, store, bootstrapRefreshToken: "bootstrap-abc");

        var session = await authenticator.GetSessionAsync();

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("grant_type=refresh_token", body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=bootstrap-abc", body, StringComparison.Ordinal);

        // The token travels in the body, never the URL, where it would land in access logs.
        var uri = Assert.Single(handler.Uris);
        Assert.Equal("https://login.test.questrade.invalid/oauth2/token", uri.ToString());
        Assert.DoesNotContain("bootstrap-abc", uri.ToString(), StringComparison.Ordinal);

        Assert.Equal("access-1", session.AccessToken);
        Assert.Equal("https://api01.iq.questrade.com/", session.ApiServer);
        Assert.Equal(Start.AddSeconds(1800 - 60), session.ExpiresAt);
    }

    [Fact]
    public async Task A_stored_token_is_preferred_over_the_bootstrap_token()
    {
        var handler = TokenEndpointHandler.Always(TokenJson());
        var store = new RecordingTokenStore { Token = "rotated-earlier" };
        var authenticator = NewAuthenticator(handler, store, bootstrapRefreshToken: "bootstrap-abc");

        await authenticator.GetSessionAsync();

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("refresh_token=rotated-earlier", body, StringComparison.Ordinal);
        Assert.DoesNotContain("bootstrap-abc", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_api_server_without_a_trailing_slash_is_normalised()
    {
        var handler = TokenEndpointHandler.Always(
            TokenJson(apiServer: "https://api07.iq.questrade.com"));
        var authenticator = NewAuthenticator(handler, new RecordingTokenStore());

        var session = await authenticator.GetSessionAsync();

        Assert.Equal("https://api07.iq.questrade.com/", session.ApiServer);
    }

    [Fact]
    public async Task The_new_refresh_token_is_persisted_before_the_session_is_returned()
    {
        // Failure mode 1: a crash between "used the old token" and "stored the new one"
        // loses the only way back in. This pins the save/return order; the test below pins
        // the harder half — that a session whose save failed is not cached either.
        var order = new List<string>();
        var handler = TokenEndpointHandler.Always(TokenJson(refreshToken: "rotated-99"));
        var store = new RecordingTokenStore(order);
        var authenticator = NewAuthenticator(handler, store);

        await authenticator.GetSessionAsync();
        order.Add("returned");

        Assert.Equal(["saved", "returned"], order);
        Assert.Equal("rotated-99", Assert.Single(store.Saved));
    }

    [Fact]
    public async Task A_session_whose_rotation_could_not_be_stored_is_never_handed_out()
    {
        // The other half of failure mode 1: if the save fails, caching the session would
        // hide the fact that the stored token is now stale for the next 30 minutes.
        const string secret = "ROTATED-DO-NOT-LEAK-1a2b";
        var handler = TokenEndpointHandler.Always(TokenJson(refreshToken: secret));
        var store = new RecordingTokenStore { ThrowOnSave = new InvalidOperationException("disk full") };
        var log = new CapturingLogger<QuestradeAuthenticator>();
        var authenticator = NewAuthenticator(handler, store, logger: log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => authenticator.GetSessionAsync());

        // The account is already lost at this instant — the presented token was consumed and
        // its replacement exists nowhere — so the log has to name it, or the re-authorization
        // prompt on the next call points at the wrong cause.
        var error = Assert.Single(log.AtLevel(LogLevel.Error));
        Assert.Contains("re-authoriz", error.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, error.Text, StringComparison.Ordinal);

        store.ThrowOnSave = null;
        await authenticator.GetSessionAsync();

        // Two refreshes: nothing was cached by the attempt that could not persist.
        Assert.Equal(2, handler.Count);
    }

    [Theory]
    [InlineData("""{"api_server":"https://api.x/","expires_in":1800,"refresh_token":"ROTATED-NEW"}""")]
    [InlineData("""{"access_token":"a","expires_in":1800,"refresh_token":"ROTATED-NEW"}""")]
    [InlineData("""{"access_token":"a","api_server":"https://api.x/","refresh_token":"ROTATED-NEW"}""")]
    [InlineData("""{"access_token":"a","api_server":"https://api.x/","expires_in":"soon","refresh_token":"ROTATED-NEW"}""")]
    public async Task A_malformed_success_response_still_persists_the_rotation(string body)
    {
        // The fourth way to lose the token, and the one the brief did not name: Questrade has
        // already consumed the presented token by the time the body is parsed, so anything
        // that throws between the exchange and the save throws away the only credential that
        // still works. The replacement is right there in the payload.
        var handler = TokenEndpointHandler.Always(body);
        var store = new RecordingTokenStore { Token = "OLD-SPENT-TOKEN" };
        var authenticator = NewAuthenticator(handler, store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => authenticator.GetSessionAsync());

        Assert.Equal("ROTATED-NEW", store.Token);
    }

    [Fact]
    public async Task A_success_response_with_no_refresh_token_requires_reauthorization()
    {
        // Nothing to persist and the presented token is spent, so this is unrecoverable in
        // exactly the way an expired token is — and must arrive as the same, actionable type.
        var handler = TokenEndpointHandler.Always(
            """{"access_token":"a","api_server":"https://api.x/","expires_in":1800}""");
        var store = new RecordingTokenStore { Token = "OLD-SPENT-TOKEN" };
        var authenticator = NewAuthenticator(handler, store);

        await Assert.ThrowsAsync<QuestradeReauthorizationRequiredException>(
            () => authenticator.GetSessionAsync());

        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task A_truncated_success_response_fails_as_an_invalid_operation()
    {
        // Unrecoverable — there is no refresh token to rescue from a body that will not parse.
        // Pinned so Tasks 7Q/8Q see the documented shape rather than a raw JsonException.
        var handler = TokenEndpointHandler.Always("""{"access_token":"a","api_ser""");
        var authenticator = NewAuthenticator(handler, new RecordingTokenStore());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.GetSessionAsync());
        Assert.Contains("JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_expires_in_sent_as_a_string_is_accepted()
    {
        // Plenty of OAuth servers send expires_in as a JSON string. Rejecting it would cost
        // the user their connection over a formatting detail.
        var handler = TokenEndpointHandler.Always(
            """{"access_token":"a","api_server":"https://api.x/","expires_in":"1800","refresh_token":"r"}""");
        var authenticator = NewAuthenticator(handler, new RecordingTokenStore());

        var session = await authenticator.GetSessionAsync();

        Assert.Equal(Start.AddSeconds(1800 - 60), session.ExpiresAt);
    }

    [Fact]
    public void A_formatted_session_never_prints_the_access_token()
    {
        // Records synthesize ToString() over every property, so one LogInformation("{Session}",
        // session) anywhere downstream would put a live bearer credential on disk.
        var session = new QuestradeSession(
            "ACCESS-SECRET-XYZ", "https://api.x/", Start.AddMinutes(29));

        var formatted = $"{session}";

        Assert.DoesNotContain("ACCESS-SECRET-XYZ", formatted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", formatted, StringComparison.Ordinal);
        // Still useful for debugging: everything that is not a credential still prints.
        Assert.Contains("https://api.x/", formatted, StringComparison.Ordinal);
        Assert.Contains("ExpiresAt", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_callers_trigger_exactly_one_refresh()
    {
        // Failure mode 2: the second concurrent refresh presents an already-consumed token
        // and both die.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = TokenEndpointHandler.Always(TokenJson());
        handler.Gate = gate;
        var authenticator = NewAuthenticator(handler, new RecordingTokenStore());

        var callers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => authenticator.GetSessionAsync()))
            .ToArray();

        // Wait for the one in-flight request, then leave the others time to pile up: without
        // single-flight they would all reach the handler during this window.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (handler.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(200);
        gate.SetResult();

        var sessions = await Task.WhenAll(callers);

        Assert.Equal(1, handler.Count);
        Assert.All(sessions, s => Assert.Same(sessions[0], s));
    }

    [Fact]
    public async Task A_cached_session_is_reused_until_it_nears_expiry()
    {
        var handler = TokenEndpointHandler.Always(TokenJson(expiresIn: 1800));
        var authenticator = NewAuthenticator(handler, new RecordingTokenStore());

        var first = await authenticator.GetSessionAsync();
        Assert.Equal(1, handler.Count);

        // Comfortably outside the 60s safety margin: no network call.
        _time.Advance(TimeSpan.FromSeconds(1700));
        Assert.Same(first, await authenticator.GetSessionAsync());
        Assert.Equal(1, handler.Count);

        // Inside the margin, with the real expiry still 55s away: refresh anyway.
        _time.Advance(TimeSpan.FromSeconds(45));
        var second = await authenticator.GetSessionAsync();
        Assert.Equal(2, handler.Count);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task An_expired_refresh_token_surfaces_as_reauthorization_required()
    {
        // Failure mode 3: idle longer than the 3-day refresh-token expiry. Unavoidable, so
        // it must arrive as something the UI can render as "reconnect to Questrade".
        var handler = TokenEndpointHandler.Always(
            """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        var authenticator = NewAuthenticator(handler, new RecordingTokenStore());

        var ex = await Assert.ThrowsAsync<QuestradeReauthorizationRequiredException>(
            () => authenticator.GetSessionAsync());

        Assert.Contains("Questrade", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-authorize", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_stored_token_and_no_bootstrap_token_requires_reauthorization()
    {
        var handler = TokenEndpointHandler.Always(TokenJson());
        var authenticator = NewAuthenticator(handler, new RecordingTokenStore(), bootstrapRefreshToken: "");

        await Assert.ThrowsAsync<QuestradeReauthorizationRequiredException>(
            () => authenticator.GetSessionAsync());

        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task A_transient_failure_does_not_wedge_the_authenticator()
    {
        var handler = new TokenEndpointHandler(n => n == 1
            ? TokenEndpointHandler.Respond("upstream boom", HttpStatusCode.InternalServerError)
            : TokenEndpointHandler.Respond(TokenJson()));
        var authenticator = NewAuthenticator(handler, new RecordingTokenStore());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.GetSessionAsync());
        Assert.IsNotType<QuestradeReauthorizationRequiredException>(ex);
        Assert.Contains("500", ex.Message, StringComparison.Ordinal);

        var session = await authenticator.GetSessionAsync();
        Assert.Equal("access-1", session.AccessToken);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Invalidate_drops_the_cached_session_without_touching_the_store()
    {
        var handler = TokenEndpointHandler.Always(TokenJson());
        var store = new RecordingTokenStore();
        var authenticator = NewAuthenticator(handler, store);

        await authenticator.GetSessionAsync();
        authenticator.Invalidate();
        await authenticator.GetSessionAsync();

        Assert.Equal(2, handler.Count);
        // A 401 means the access token is stale, not that the refresh token is bad: the
        // second refresh must present the token the first one rotated to.
        Assert.Contains("refresh_token=rotated-1", handler.Bodies[1], StringComparison.Ordinal);
        // And must have re-read it from the store rather than remembering it in the process.
        Assert.Equal(2, store.ReadCount);
    }

    [Fact]
    public async Task No_token_value_appears_in_logs_or_exception_messages()
    {
        const string secret = "REFRESH-DO-NOT-LEAK-9f3a";
        const string rotated = "ROTATED-DO-NOT-LEAK-7b2c";
        // The access token is a live bearer credential for the next 30 minutes and belongs in
        // this set as much as the refresh tokens do — leaving it out is what let a log of the
        // whole QuestradeSession record pass unnoticed.
        const string access = "ACCESS-DO-NOT-LEAK-4d1e";

        // Success: neither the rotated token nor the access token may be logged.
        var okLogger = new CapturingLogger<QuestradeAuthenticator>();
        var okHandler = TokenEndpointHandler.Always(
            TokenJson(accessToken: access, refreshToken: rotated));
        var session = await NewAuthenticator(
            okHandler, new RecordingTokenStore(), secret, okLogger).GetSessionAsync();

        // 400: the re-authorization path.
        var badLogger = new CapturingLogger<QuestradeAuthenticator>();
        var badHandler = TokenEndpointHandler.Always(
            $$"""{"error":"invalid_grant","token":"{{secret}}"}""", HttpStatusCode.BadRequest);
        var reauth = await Assert.ThrowsAsync<QuestradeReauthorizationRequiredException>(
            () => NewAuthenticator(badHandler, new RecordingTokenStore(), secret, badLogger).GetSessionAsync());

        // 500: the transient path.
        var errorLogger = new CapturingLogger<QuestradeAuthenticator>();
        var errorHandler = TokenEndpointHandler.Always(secret, HttpStatusCode.InternalServerError);
        var transient = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewAuthenticator(errorHandler, new RecordingTokenStore(), secret, errorLogger).GetSessionAsync());

        // The session really is carrying the secret, so the assertions below are about
        // redaction rather than about the value never having existed.
        Assert.Equal(access, session.AccessToken);

        foreach (var text in new[]
                 {
                     okLogger.AllText, badLogger.AllText, errorLogger.AllText,
                     reauth.ToString(), transient.ToString(), session.ToString()
                 })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(rotated, text, StringComparison.Ordinal);
            Assert.DoesNotContain(access, text, StringComparison.Ordinal);
        }
    }
}
