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
    /// Seeds the questrade_token row directly, protected with the same on-disk key ring the
    /// app under test will use — modelling a real "dead token survives a restart" lockout
    /// rather than a token the app could never have decrypted in the first place.
    /// </summary>
    private async Task SeedStoredTokenAsync(string plaintextToken)
    {
        Directory.CreateDirectory(_keysDir);
        var keys = DataProtectionProvider.Create(new DirectoryInfo(_keysDir));
        var protector = keys.CreateProtector("StonkWatch.Questrade.RefreshToken");
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
    public async Task Authorize_stores_the_token_and_reports_success()
    {
        // Models a real lockout: a dead token is already stored (surviving a restart, same
        // key ring), and only a freshly submitted token is accepted by "Questrade". This is
        // the amendment's recovery path, verified against the built code rather than assumed:
        // SaveAsync must overwrite the dead row, Invalidate() must drop any cached session, and
        // GetSessionAsync must then present the *new* token rather than the one already stored.
        await SeedStoredTokenAsync("DEAD-STORED-TOKEN");

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
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/questrade/authorize", new AuthorizeQuestradeRequest("GOOD-NEW-TOKEN"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Exactly one request reached "Questrade", and it presented the new token, not the
        // dead one that was sitting in the store beforehand.
        var request = Assert.Single(seenBodies);
        Assert.Contains("refresh_token=GOOD-NEW-TOKEN", request, StringComparison.Ordinal);
        Assert.DoesNotContain("DEAD-STORED-TOKEN", request, StringComparison.Ordinal);

        // And a follow-up status call reports connected without needing to re-authorize —
        // proving the rotated token actually landed in the store, not just in memory.
        var status = await client.GetFromJsonAsync<QuestradeStatusDto>("/api/questrade/status");
        Assert.NotNull(status);
        Assert.True(status.Connected);
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
    public async Task The_questrade_routes_require_authorization()
    {
        // Enabled specifically so a 404 from the disabled-feature gate can't be mistaken for
        // the auth guard doing its job.
        using var factory = NewFactory(questradeAuthHandler: AlwaysOk());
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/questrade/status");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect,
            $"expected a challenge, got {response.StatusCode}");
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
