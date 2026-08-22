using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Services.MarketData.Questrade;

namespace StonkWatch.Web.Tests;

/// <summary>
/// <c>GET /api/watchlist/search</c>, the route behind the sidebar's add box. The Questrade call
/// itself is covered by <see cref="QuestradeSymbolSearchTests"/>; what these pin is the adapter
/// around it — above all that a server without Questrade says so, because the box still adds a
/// typed ticker there and the sidebar needs to tell the difference between "off" and "broken".
/// </summary>
[Collection(PostgresCollection.Name)]
public class WatchlistSearchEndpointTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string TestApiKey = "test-api-key";

    private readonly string _keysDir = Path.Combine(
        Path.GetTempPath(), "stonkwatch-search-dp-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Directory.Exists(_keysDir))
        {
            Directory.Delete(_keysDir, recursive: true);
        }
    }

    /// <summary>Stands in for the real Questrade-backed search and records what it was asked.</summary>
    private sealed class StubSearch(
        Func<string, int, IReadOnlyList<SymbolSearchResultDto>> respond) : IQuestradeSymbolSearch
    {
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<SymbolSearchResultDto>> SearchAsync(
            string prefix, int limit, CancellationToken ct = default)
        {
            Queries.Add(prefix);
            return Task.FromResult(respond(prefix, limit));
        }
    }

    private WebApplicationFactory<Program> NewFactory(
        bool questradeEnabled, IQuestradeSymbolSearch? search = null) =>
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
            // Program.cs refuses to start with Questrade on and no key-ring path, because the
            // encrypted refresh token would stop decrypting on the next restart.
            builder.UseSetting("DataProtectionKeysPath", questradeEnabled ? _keysDir : "");

            if (search is not null)
            {
                // Registered after the app's own, so this is what the endpoint resolves — no
                // socket is opened at Questrade in any of these tests.
                builder.ConfigureTestServices(services => services.AddSingleton(search));
            }
        });

    private static HttpClient NewClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        return client;
    }

    private static SymbolSearchResultDto Nvidia => new("NVDA", "NVIDIA CORP", "NASDAQ", 8049);

    [Fact]
    public async Task Search_is_authenticated()
    {
        using var factory = NewFactory(questradeEnabled: false);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/watchlist/search?q=NVDA");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect,
            $"expected a challenge, got {response.StatusCode}");
    }

    [Fact]
    public async Task With_questrade_off_search_explains_itself_instead_of_404ing()
    {
        using var factory = NewFactory(questradeEnabled: false);
        using var client = NewClient(factory);

        var response = await client.GetAsync("/api/watchlist/search?q=NVDA");

        // The route is mapped either way. A 404 here would be indistinguishable from a typo
        // in the sidebar's own URL, and the box would have no way to say what is actually
        // wrong — while typing a ticker and pressing Enter still works on this server.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Questrade", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_blank_query_answers_empty_without_calling_questrade()
    {
        var stub = new StubSearch((_, _) => [Nvidia]);
        using var factory = NewFactory(questradeEnabled: true, stub);
        using var client = NewClient(factory);

        var response = await client.GetAsync("/api/watchlist/search?q=");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<SymbolSearchResultDto>>())!);
        // An empty box must not become a prefix match on everything Questrade lists.
        Assert.Empty(stub.Queries);
    }

    [Fact]
    public async Task A_missing_query_parameter_is_treated_the_same_as_a_blank_one()
    {
        var stub = new StubSearch((_, _) => [Nvidia]);
        using var factory = NewFactory(questradeEnabled: true, stub);
        using var client = NewClient(factory);

        var response = await client.GetAsync("/api/watchlist/search");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(stub.Queries);
    }

    [Fact]
    public async Task Matches_are_returned_with_the_fields_the_sidebar_renders()
    {
        var stub = new StubSearch((_, _) => [Nvidia]);
        using var factory = NewFactory(questradeEnabled: true, stub);
        using var client = NewClient(factory);

        var results = await client.GetFromJsonAsync<List<SymbolSearchResultDto>>(
            "/api/watchlist/search?q=NVDA");

        var only = Assert.Single(results!);
        Assert.Equal("NVDA", only.Symbol);
        Assert.Equal("NVIDIA CORP", only.Description);
        Assert.Equal("NASDAQ", only.Exchange);
        Assert.Equal(8049, only.SymbolId);
        Assert.Equal("NVDA", Assert.Single(stub.Queries));
    }

    [Fact]
    public async Task The_endpoint_caps_how_many_matches_it_asks_for()
    {
        var limits = new List<int>();
        var stub = new StubSearch((_, limit) => { limits.Add(limit); return []; });
        using var factory = NewFactory(questradeEnabled: true, stub);
        using var client = NewClient(factory);

        await client.GetAsync("/api/watchlist/search?q=A");

        // A one-letter prefix matches hundreds of listings. The cap is the endpoint's, not
        // the caller's — nothing in the query string can raise it.
        Assert.Equal(10, Assert.Single(limits));
    }

    [Fact]
    public async Task An_upstream_failure_is_reported_as_unavailable_not_as_an_empty_result()
    {
        var stub = new StubSearch((_, _) => throw new HttpRequestException("questrade is down"));
        using var factory = NewFactory(questradeEnabled: true, stub);
        using var client = NewClient(factory);

        var response = await client.GetAsync("/api/watchlist/search?q=NVDA");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // Fixed text. The exception's own message is never echoed: an upstream failure is
        // one of the places a token or an api_server URL could leak into a response.
        Assert.DoesNotContain("questrade is down", body);
    }

    [Fact]
    public async Task A_token_questrade_will_not_renew_is_reported_as_unavailable()
    {
        var stub = new StubSearch((_, _) =>
            throw new QuestradeReauthorizationRequiredException("re-authorize"));
        using var factory = NewFactory(questradeEnabled: true, stub);
        using var client = NewClient(factory);

        var response = await client.GetAsync("/api/watchlist/search?q=NVDA");

        // Reaching the search with a dead refresh token is the likeliest real failure here,
        // and it must not escape as a 500 the sidebar renders as a broken search.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
