using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StonkWatch.Web.Tests;

/// <summary>
/// The sidebar is injected into <c>_Layout.cshtml</c>, so it renders on every page. These pin
/// the two halves of that wiring a refactor is most likely to break silently: the anonymous
/// pages must not get it, and the two static assets it needs must actually be served.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WatchlistSidebarLayoutTests(PostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private WebApplicationFactory<Program> NewFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:StonkWatch", fixture.ConnectionString);
            builder.UseSetting("Auth:ApiKey", "test-api-key");
            builder.UseSetting("Auth:AllowedEmail", "test@example.com");
            builder.UseSetting("Auth:Google:ClientId", "test-client-id");
            builder.UseSetting("Auth:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("LiveWatchlist:Enabled", "false");
            builder.UseSetting("Monitoring:Enabled", "false");
        });

    [Fact]
    public async Task The_login_page_does_not_render_the_sidebar()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Account/Login");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // An anonymous visitor has no watchlist, and every /api/watchlist route is
        // authenticated — a sidebar here would poll its way to a 401 loop.
        //
        // Note what this does and does not pin. Today the login page renders through
        // _LoginLayout, which has no sidebar markup at all, so this passes without
        // exercising _Layout's `User.Identity?.IsAuthenticated` guard. It is still worth
        // keeping: it is the property a reader actually cares about, and it catches the
        // realistic regression of the sidebar being moved into a shared layout or
        // _LoginLayout being pointed at _Layout. The auth guard itself is unpinned —
        // asserting it needs an authenticated cookie session, and this app signs in
        // through Google OAuth, which no test drives.
        Assert.DoesNotContain("watchlist-sidebar", html);
        Assert.DoesNotContain("watchlist.js", html);
    }

    [Theory]
    [InlineData("/css/watchlist.css")]
    [InlineData("/js/watchlist.js")]
    public async Task The_sidebar_assets_are_served(string path)
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(await response.Content.ReadAsStringAsync());
    }
}
