using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using StonkWatch.Web.Contracts;

namespace StonkWatch.Web.Tests;

[Collection(PostgresCollection.Name)]
public class WatchlistEndpointsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string TestApiKey = "test-api-key";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private WebApplicationFactory<Program> NewFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:StonkWatch", fixture.ConnectionString);
            builder.UseSetting("Auth:ApiKey", TestApiKey);
            builder.UseSetting("Auth:AllowedEmail", "test@example.com");
            // Never used — no test signs in with Google — but Program.cs refuses to
            // start without them.
            builder.UseSetting("Auth:Google:ClientId", "test-client-id");
            builder.UseSetting("Auth:Google:ClientSecret", "test-client-secret");
            // Left off: these tests cover the CRUD routes, which must work with the
            // live feed disabled. No upstream socket is opened.
            builder.UseSetting("LiveWatchlist:Enabled", "false");
            builder.UseSetting("Monitoring:Enabled", "false");
        });

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        using var factory = NewFactory();
        // Redirects off: the cookie scheme challenges with a 302 to /Account/Login, and
        // a client that follows it would report the login page's 200 instead.
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/watchlist");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect,
            $"expected a challenge, got {response.StatusCode}");
    }

    [Fact]
    public async Task An_api_key_request_can_add_and_read_a_symbol()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        var created = await client.PostAsJsonAsync(
            "/api/watchlist/items", new CreateWatchlistItemRequest("ASTS"));
        created.EnsureSuccessStatusCode();

        var view = await client.GetFromJsonAsync<WatchlistViewDto>("/api/watchlist");

        var row = Assert.Single(view!.Rows);
        Assert.Equal("ASTS", row.Symbol);
        // No quote has arrived, so every price field must be null rather than zero.
        Assert.Null(row.Last);
        Assert.Null(row.ChangePercent);
    }

    [Fact]
    public async Task Adding_a_duplicate_symbol_returns_409()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        await client.PostAsJsonAsync("/api/watchlist/items", new CreateWatchlistItemRequest("ASTS"));

        var second = await client.PostAsJsonAsync(
            "/api/watchlist/items", new CreateWatchlistItemRequest("ASTS"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Adding_an_empty_symbol_returns_400()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/watchlist/items", new CreateWatchlistItemRequest("  "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
