using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Services.MarketData;

namespace StonkWatch.Web.Tests;

[Collection(PostgresCollection.Name)]
public class WatchlistEndpointsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string TestApiKey = "test-api-key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private WebApplicationFactory<Program> NewFactory(bool liveWatchlistEnabled = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:StonkWatch", fixture.ConnectionString);
            builder.UseSetting("Auth:ApiKey", TestApiKey);
            builder.UseSetting("Auth:AllowedEmail", "test@example.com");
            // Never used, no test signs in with Google, but Program.cs refuses to
            // start without them.
            builder.UseSetting("Auth:Google:ClientId", "test-client-id");
            builder.UseSetting("Auth:Google:ClientSecret", "test-client-secret");
            // Off for the CRUD-only tests, which must work with the live feed disabled
            // and open no upstream socket. The SSE tests below flip this on.
            builder.UseSetting("LiveWatchlist:Enabled", liveWatchlistEnabled ? "true" : "false");
            builder.UseSetting("Monitoring:Enabled", "false");
        });

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        using var factory = NewFactory();
        // Redirects off: the cookie scheme challenges with a 302 to /Account/Login, and
        // a client that follows it would report the login page as a 200 instead.
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

    [Fact]
    public async Task Stream_returns_503_when_the_live_feed_is_disabled()
    {
        using var factory = NewFactory(); // LiveWatchlist:Enabled = false
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        var response = await client.GetAsync("/api/watchlist/stream");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Stream_delivers_a_pushed_quote_to_a_connected_client()
    {
        using var factory = NewFactory(liveWatchlistEnabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

        await client.PostAsJsonAsync("/api/watchlist/items", new CreateWatchlistItemRequest("ASTS"));

        // Bounded so a delivery failure fails the test instead of hanging until the
        // harness kills it.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/watchlist/stream");
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Opening burst: one row for the symbol just added, no quote yet.
        var burstRow = DeserializeRow(await ReadNextEventDataAsync(reader, cts.Token));
        Assert.Equal("ASTS", burstRow.Symbol);
        Assert.Null(burstRow.Last);

        var cache = factory.Services.GetRequiredService<LiveQuoteCache>();

        // The endpoint registers its cache subscription only after the burst has been
        // produced (see LiveQuoteCache.SubscribeAsync remarks), so wait for that
        // registration rather than pushing blind: pushing before it lands would be
        // silently dropped, which is exactly the false-pass this test must not have.
        await WaitForAsync(() => cache.SubscriberCount == 1, cts.Token);
        Assert.Equal(1, cache.SubscriberCount);

        cache.ApplyTrade(new Trade("ASTS", 123.45m, DateTimeOffset.UtcNow));

        var tickRow = DeserializeRow(await ReadNextEventDataAsync(reader, cts.Token));
        Assert.Equal("ASTS", tickRow.Symbol);
        Assert.Equal(123.45m, tickRow.Last);
    }

    [Fact]
    public async Task Disconnecting_a_stream_client_unsubscribes_from_the_cache()
    {
        using var factory = NewFactory(liveWatchlistEnabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        await client.PostAsJsonAsync("/api/watchlist/items", new CreateWatchlistItemRequest("ASTS"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cache = factory.Services.GetRequiredService<LiveQuoteCache>();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/watchlist/stream");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var reader = new StreamReader(stream);

        // Drain the burst so the subscription has actually registered before measuring it.
        await ReadNextEventDataAsync(reader, cts.Token);
        await WaitForAsync(() => cache.SubscriberCount == 1, cts.Token);
        Assert.Equal(1, cache.SubscriberCount);

        // Simulates the browser navigating away or closing the tab: tear down the
        // client side of the connection without the server having offered to close it.
        reader.Dispose();
        response.Dispose();
        request.Dispose();

        await WaitForAsync(() => cache.SubscriberCount == 0, cts.Token);
        Assert.Equal(0, cache.SubscriberCount);
    }

    /// <summary>
    /// Reads lines until the next SSE "data:" field, ignoring "event:", blank, and id
    /// lines. Bounded by <paramref name="ct"/>: a missing event either times out (the
    /// token fires) or the stream closes and this throws; it never hangs silently.
    /// </summary>
    private static async Task<string> ReadNextEventDataAsync(StreamReader reader, CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                return line["data:".Length..].TrimStart();
            }
        }

        throw new InvalidOperationException("SSE stream ended before a data event arrived.");
    }

    private static async Task WaitForAsync(
        Func<bool> condition, CancellationToken ct, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, ct);
        }
    }

    private static WatchlistRowDto DeserializeRow(string json) =>
        JsonSerializer.Deserialize<WatchlistRowDto>(json, JsonOptions)!;
}
