using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StonkWatch.Web.Services.MarketData.Questrade;
using StonkWatch.Web.Services.Watchlist;

namespace StonkWatch.Web.Tests;

/// <summary>
/// No existing test boots the host with <c>Questrade:Enabled</c> and
/// <c>LiveWatchlist:Enabled</c> both true — <see cref="WatchlistEndpointsTests"/> exercises the
/// live watchlist alone, and <see cref="QuestradeEndpointsTests"/> exercises Questrade alone.
/// Task 8Q's entire deliverable is the six registrations in <c>Program.cs</c> that only exist
/// behind that combination, so this file is what actually boots the host both flags true and
/// proves each one resolves. Accessing <see cref="WebApplicationFactory{TEntryPoint}.Services"/>
/// starts the real host, including hosted services' <c>StartAsync</c> — safe here because
/// <see cref="LiveWatchlistPollJob.ExecuteAsync"/> returns before touching the resolver,
/// authenticator, or quote client whenever the watchlist has no symbols, and this test's
/// database is reset empty for every test.
/// </summary>
[Collection(PostgresCollection.Name)]
public class QuestradeWiringTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string TestApiKey = "test-api-key";

    private readonly string _keysDir = Path.Combine(
        Path.GetTempPath(), "stonkwatch-questrade-wiring-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Directory.Exists(_keysDir))
        {
            Directory.Delete(_keysDir, recursive: true);
        }
    }

    private WebApplicationFactory<Program> NewFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:StonkWatch", fixture.ConnectionString);
            builder.UseSetting("Auth:ApiKey", TestApiKey);
            builder.UseSetting("Auth:AllowedEmail", "test@example.com");
            builder.UseSetting("Auth:Google:ClientId", "test-client-id");
            builder.UseSetting("Auth:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("Monitoring:Enabled", "false");
            builder.UseSetting("Questrade:Enabled", "true");
            builder.UseSetting("Questrade:LoginUrl", "https://questrade.test.invalid/oauth2/token");
            builder.UseSetting("Questrade:BootstrapRefreshToken", "");
            builder.UseSetting("LiveWatchlist:Enabled", "true");
            // The fail-fast guard in Program.cs is correct and already pinned in both
            // directions elsewhere; a real path here is what lets this test get past it.
            builder.UseSetting("DataProtectionKeysPath", _keysDir);
        });

    [Fact]
    public async Task Enabling_Questrade_and_the_live_watchlist_together_registers_all_six_dependencies()
    {
        using var factory = NewFactory();
        var services = factory.Services;

        Assert.NotNull(services.GetService<IQuestradeAuthenticator>());
        Assert.NotNull(services.GetService<IQuestradeSymbolResolver>());
        Assert.NotNull(services.GetService<IQuestradeQuoteClient>());

        await using (var scope = services.CreateAsyncScope())
        {
            Assert.NotNull(scope.ServiceProvider.GetService<IQuestradeTokenStore>());
            Assert.NotNull(scope.ServiceProvider.GetService<LiveWatchlistPollJob>());
        }

        var hostedServices = services.GetServices<IHostedService>();
        Assert.Contains(hostedServices, s => s is LiveWatchlistPollWorker);
    }

    [Fact]
    public void The_QuestradeAuth_named_client_carries_the_20_second_timeout()
    {
        // The only bound on a refresh held under QuestradeAuthenticator's single-flight
        // semaphore — RefreshAsync deliberately never propagates the caller's
        // CancellationToken, so a missing or reverted timeout here would hold that gate closed
        // for the HttpClient default (100s) instead.
        using var factory = NewFactory();

        var client = factory.Services
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("QuestradeAuth");

        Assert.Equal(TimeSpan.FromSeconds(20), client.Timeout);
    }
}
