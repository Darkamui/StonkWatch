using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using StonkWatch.Web.Contracts;

namespace StonkWatch.Web.Tests;

/// <summary>
/// End-to-end coverage of the two Questrade authorize endpoints: whether the app can currently
/// reach Questrade, and the one-time hand-off of a refresh token from the Questrade portal into
/// the app. The outbound call to Questrade's token endpoint is stubbed by replacing the named
/// "QuestradeAuth" HttpClient's primary handler; everything else (the DB-backed token store,
/// real Data Protection, the real QuestradeAuthenticator) runs exactly as it does in production,
/// because the whole point of several of these tests is to prove the wiring actually works
/// rather than assume it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class QuestradeEndpointsTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string TestApiKey = "test-api-key";

    private readonly string _keysDir = Path.Combine(
        Path.GetTempPath(), "stonkwatch-questrade-dp-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Directory.Exists(_keysDir))
        {
            Directory.Delete(_keysDir, recursive: true);
        }
    }

    // ---- test double for the outbound Questrade token endpoint --------------------------

    /// <summary>Stands in for Questrade's token endpoint via a named-client handler swap.</summary>
    private sealed class DelegatingStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string TokenJson(string refreshToken = "rotated-1") =>
        $$"""
          {"access_token":"access-1","api_server":"https://api01.iq.questrade.com/",
           "expires_in":1800,"refresh_token":"{{refreshToken}}","token_type":"Bearer"}
          """;

    /// <summary>Answers OK to every request, whatever token was presented.</summary>
    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> AlwaysOk(string refreshToken = "rotated-1") =>
        _ => Task.FromResult(Json(TokenJson(refreshToken)));

    /// <summary>Answers OK only to a request presenting <paramref name="liveToken"/>; 400 otherwise.</summary>
    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> AcceptingOnly(
        string liveToken, string rotatedTo) =>
        async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            return body.Contains($"refresh_token={liveToken}", StringComparison.Ordinal)
                ? Json(TokenJson(rotatedTo))
                : Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        };

    /// <summary>Answers 400 invalid_grant to every request, whatever token was presented.</summary>
    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> AlwaysRejects() =>
        _ => Task.FromResult(Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));

    /// <summary>Answers 500 to every request — a transient Questrade outage, not a bad token.</summary>
    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> AlwaysFails() =>
        _ => Task.FromResult(Json("""{"error":"server_error"}""", HttpStatusCode.InternalServerError));

    // ---- factory --------------------------------------------------------------------------

    private WebApplicationFactory<Program> NewFactory(
        bool questradeEnabled = true,
        string bootstrapRefreshToken = "",
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? questradeAuthHandler = null,
        bool includeDataProtectionKeysPath = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:StonkWatch", fixture.ConnectionString);
            builder.UseSetting("Auth:ApiKey", TestApiKey);
            builder.UseSetting("Auth:AllowedEmail", "test@example.com");
            builder.UseSetting("Auth:Google:ClientId", "test-client-id");
            builder.UseSetting("Auth:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("Monitoring:Enabled", "false");
            builder.UseSetting("LiveWatchlist:Enabled", "false");
            builder.UseSetting("Questrade:Enabled", questradeEnabled ? "true" : "false");
            builder.UseSetting("Questrade:LoginUrl", "https://questrade.test.invalid/oauth2/token");
            builder.UseSetting("Questrade:BootstrapRefreshToken", bootstrapRefreshToken);
            builder.UseSetting(
                "DataProtectionKeysPath", includeDataProtectionKeysPath ? _keysDir : "");

            if (questradeAuthHandler is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddHttpClient("QuestradeAuth")
                        .ConfigurePrimaryHttpMessageHandler(() => new DelegatingStub(questradeAuthHandler));
                });
            }
        });

    /// <summary>
    /// Seeds the questrade_token row directly, protected with the running app's own
    /// <see cref="IDataProtectionProvider"/> — not a freestanding one built from the same
    /// key-ring directory. Data Protection's purpose chain includes the application
    /// discriminator (<c>SetApplicationName("StonkWatch")</c> in Program.cs), and
    /// <see cref="DataProtectionProvider.Create(DirectoryInfo)"/> assigns a different one, so
    /// ciphertext from that overload is silently undecryptable by the app under test — it comes
    /// back <c>null</c> from <c>ReadAsync</c>, and a test built on it would pass with the seed
    /// commented out entirely. Resolving the provider from <paramref name="factory"/> after it
    /// exists is what makes this a real "dead token survives a restart" lockout rather than a
    /// token the app could never have decrypted in the first place.
    /// </summary>
    private async Task SeedStoredTokenAsync(
        WebApplicationFactory<Program> factory, string plaintextToken)
    {
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("StonkWatch.Questrade.RefreshToken");
        var ciphertext = protector.Protect(plaintextToken);

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO questrade_token (id, protected_refresh_token, updated_at)
             VALUES (1, {ciphertext}, now())
             """);
    }

    // ---- tests ------------------------------------------------------------------------------

    [Fact]
    public async Task Status_reports_connected_when_a_session_is_available()
    {
        // GetSessionAsync only ever attempts a refresh when it has a token to try — a
        // bootstrap token is the simplest way to give it one.
        using var factory = NewFactory(
            bootstrapRefreshToken: "bootstrap-token", questradeAuthHandler: AlwaysOk());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        var status = await client.GetFromJsonAsync<QuestradeStatusDto>("/api/questrade/status");

        Assert.NotNull(status);
        Assert.True(status.Connected);
        Assert.Null(status.Reason);
    }

    [Fact]
    public async Task Status_reports_disconnected_when_reauthorization_is_required()
    {
        // No bootstrap token and nothing stored: GetSessionAsync has nothing to try, so it
        // fails before ever making a network call.
        using var factory = NewFactory(bootstrapRefreshToken: "");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        var response = await client.GetAsync("/api/questrade/status");
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<QuestradeStatusDto>();

        Assert.NotNull(status);
        Assert.False(status.Connected);
        Assert.False(string.IsNullOrWhiteSpace(status.Reason));
    }

    [Fact]
    public async Task Status_reports_disconnected_instead_of_500_when_Questrade_is_unreachable()
    {
        // A transient Questrade 5xx surfaces from GetSessionAsync as InvalidOperationException,
        // not QuestradeReauthorizationRequiredException — the contract is "connected means the
        // session call succeeded", and a transient failure must still answer 200 with
        // connected:false, not escape as a 500 from the one endpoint operations.md tells an
        // operator to check when something looks wrong.
        using var factory = NewFactory(
            bootstrapRefreshToken: "bootstrap-token", questradeAuthHandler: AlwaysFails());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        var response = await client.GetAsync("/api/questrade/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<QuestradeStatusDto>();
        Assert.NotNull(status);
        Assert.False(status.Connected);
        Assert.False(string.IsNullOrWhiteSpace(status.Reason));
    }

    [Fact]
    public async Task Authorize_stores_the_token_and_reports_success()
    {
        var seenBodies = new List<string>();
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler = async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            seenBodies.Add(body);
            return body.Contains("refresh_token=GOOD-NEW-TOKEN", StringComparison.Ordinal)
                ? Json(TokenJson("rotated-after-recovery"))
                : Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        };

        using var factory = NewFactory(questradeAuthHandler: handler);

        // Models a real lockout: a dead token is already stored (surviving a restart, same
        // Data Protection provider the app itself uses), and only a freshly submitted token is
        // accepted by "Questrade". This is the amendment's recovery path, verified against the
        // built code rather than assumed: SaveAsync must overwrite the dead row, Invalidate()
        // must drop any cached session, and GetSessionAsync must then present the *new* token
        // rather than the one already stored.
        await SeedStoredTokenAsync(factory, "DEAD-STORED-TOKEN");

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        // Proves the seed is real, not inert: with no BootstrapRefreshToken configured,
        // GetSessionAsync only has a token to try at all if ReadAsync actually decrypted the
        // seeded row. If the seed's ciphertext were unreadable by the app (the exact bug this
        // test exists to catch), ReadAsync would return null, RefreshAsync would throw before
        // ever calling "Questrade", and seenBodies would stay empty — a fresh store and a dead
        // one would then be indistinguishable from here on, which is precisely what made the
        // old version of this test worthless with the seed commented out.
        var preCheck = await client.GetFromJsonAsync<QuestradeStatusDto>("/api/questrade/status");
        Assert.NotNull(preCheck);
        Assert.False(preCheck.Connected);
        var preCheckRequest = Assert.Single(seenBodies);
        Assert.Contains("refresh_token=DEAD-STORED-TOKEN", preCheckRequest, StringComparison.Ordinal);

        var response = await client.PostAsJsonAsync(
            "/api/questrade/authorize", new AuthorizeQuestradeRequest("GOOD-NEW-TOKEN"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The submitted token must never come back, success or failure — the rejected-token
        // test below pins the error branch; this pins the success branch, which is the one an
        // operator actually hits with a live credential in it.
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("GOOD-NEW-TOKEN", responseBody, StringComparison.Ordinal);

        // A second request reached "Questrade", and it presented the new token, not the dead
        // one that was sitting in the store beforehand.
        Assert.Equal(2, seenBodies.Count);
        var request = seenBodies[1];
        Assert.Contains("refresh_token=GOOD-NEW-TOKEN", request, StringComparison.Ordinal);
        Assert.DoesNotContain("DEAD-STORED-TOKEN", request, StringComparison.Ordinal);

        // And a follow-up status call reports connected without needing to re-authorize —
        // proving the rotated token actually landed in the store, not just in memory.
        var status = await client.GetFromJsonAsync<QuestradeStatusDto>("/api/questrade/status");
        Assert.NotNull(status);
        Assert.True(status.Connected);
    }

    [Fact]
    public async Task Authorize_invalidates_the_cached_session_so_the_new_token_is_actually_presented()
    {
        // Unlike Authorize_stores_the_token_and_reports_success, this establishes a *live,
        // unexpired* cached session before authorizing — the case the other test cannot reach,
        // because a fresh factory never has one. If the handler skipped Invalidate(),
        // GetSessionAsync's TryGetLiveSession fast path would return the cached session without
        // ever presenting the newly submitted token to "Questrade" — /authorize would still
        // report success, just not honestly. The stub only accepts OLD-TOKEN once (the initial
        // bootstrap refresh) and NEW-TOKEN thereafter, so a second presentation of OLD-TOKEN —
        // which is all a skipped Invalidate() would ever produce, since the cached path makes
        // no outbound call at all — would either be silently absent from seenBodies or rejected.
        var oldTokenUses = 0;
        var seenBodies = new List<string>();
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler = async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            seenBodies.Add(body);

            if (body.Contains("refresh_token=NEW-TOKEN", StringComparison.Ordinal))
            {
                return Json(TokenJson("rotated-after-reauthorize"));
            }

            if (body.Contains("refresh_token=OLD-TOKEN", StringComparison.Ordinal)
                && Interlocked.Increment(ref oldTokenUses) == 1)
            {
                return Json(TokenJson("should-never-be-read"));
            }

            return Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        };

        using var factory = NewFactory(bootstrapRefreshToken: "OLD-TOKEN", questradeAuthHandler: handler);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        // Establishes a live cached session via the bootstrap token, well inside the
        // 1800s-minus-margin cached fast path for the rest of this test.
        var initialStatus = await client.GetFromJsonAsync<QuestradeStatusDto>("/api/questrade/status");
        Assert.NotNull(initialStatus);
        Assert.True(initialStatus.Connected);
        Assert.Single(seenBodies);

        var response = await client.PostAsJsonAsync(
            "/api/questrade/authorize", new AuthorizeQuestradeRequest("NEW-TOKEN"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // A second request actually reached "Questrade", and it carried the newly submitted
        // token — not silence from a cached session Invalidate() failed to drop.
        Assert.Equal(2, seenBodies.Count);
        Assert.Contains("refresh_token=NEW-TOKEN", seenBodies[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public async Task Authorize_with_an_empty_token_returns_400(string refreshTokenJsonLiteral)
    {
        // No handler stubbed: the guard must return 400 before any network call is attempted,
        // so a real DNS failure here would mean the guard didn't run first.
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        using var content = new StringContent(
            $$"""{"refreshToken":{{refreshTokenJsonLiteral}}}""",
            System.Text.Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await client.PostAsync("/api/questrade/authorize", content, cts.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Authorize_with_a_token_Questrade_rejects_returns_400_and_does_not_echo_it()
    {
        const string secret = "REJECTED-TOKEN-DO-NOT-ECHO-9f3a";
        using var factory = NewFactory(questradeAuthHandler: AlwaysRejects());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/questrade/authorize", new AuthorizeQuestradeRequest(secret));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_formatted_authorize_request_never_prints_the_refresh_token()
    {
        // Records synthesize ToString() over every property, and this one carries a live
        // Questrade credential straight off the wire. Any structured log call touching the
        // request — logger.LogInformation("{Request}", request) — goes through this same
        // ToString()/PrintMembers path, so pinning it here catches that mutation without
        // needing to capture ASP.NET Core's logging pipeline over HTTP.
        var request = new AuthorizeQuestradeRequest("SUPER-SECRET-TOKEN");

        var formatted = $"{request}";

        Assert.DoesNotContain("SUPER-SECRET-TOKEN", formatted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", formatted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "/api/questrade/status")]
    [InlineData("POST", "/api/questrade/authorize")]
    public async Task The_questrade_routes_require_authorization(string method, string path)
    {
        // Both routes, not just /status: /authorize is the one that accepts and persists a
        // credential, and a mutation that moved only it off the auth group left an earlier,
        // more narrowly-named version of this test fully green. Enabled specifically so a 404
        // from the disabled-feature gate can't be mistaken for the auth guard doing its job.
        using var factory = NewFactory(questradeAuthHandler: AlwaysOk());
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new AuthorizeQuestradeRequest("irrelevant"));
        }

        var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect,
            $"expected a challenge for {method} {path}, got {response.StatusCode}");
    }

    [Fact]
    public async Task The_questrade_routes_are_absent_when_the_feature_is_disabled()
    {
        using var factory = NewFactory(
            questradeEnabled: false, includeDataProtectionKeysPath: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        var response = await client.GetAsync("/api/questrade/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Startup_fails_when_Questrade_is_enabled_without_a_data_protection_keys_path()
    {
        using var factory = NewFactory(includeDataProtectionKeysPath: false);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);

        // Unwrap to find the InvalidOperationException Program.cs actually throws — the test
        // host wraps startup failures (TargetInvocationException and friends).
        var inner = (Exception?)ex;
        while (inner is not InvalidOperationException && inner?.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        Assert.IsType<InvalidOperationException>(inner);
        Assert.Contains("Questrade:Enabled", inner.Message, StringComparison.Ordinal);
        Assert.Contains("DataProtectionKeysPath", inner.Message, StringComparison.Ordinal);
    }
}
