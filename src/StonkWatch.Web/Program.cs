using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StonkWatch.Web.Auth;
using StonkWatch.Web.Data;
using StonkWatch.Web.Endpoints;
using StonkWatch.Web.Services;
using StonkWatch.Web.Services.MarketData;
using StonkWatch.Web.Services.MarketData.Questrade;
using StonkWatch.Web.Services.Monitoring;
using StonkWatch.Web.Services.Notifications;
using StonkWatch.Web.Services.Watchlist;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Error");
});

var connectionString = builder.Configuration.GetConnectionString("StonkWatch")
    ?? throw new InvalidOperationException(
        "Connection string 'StonkWatch' is not configured. Set ConnectionStrings__StonkWatch.");

builder.Services.AddDbContext<StonkWatchDbContext>(options =>
    options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CandidateService>();

builder.Services.AddHealthChecks().AddNpgSql(connectionString, name: "postgres");

builder.Services.AddOptions<AppOptions>()
    .Bind(builder.Configuration.GetSection(AppOptions.SectionName));

builder.Services.AddOptions<MonitoringOptions>()
    .Bind(builder.Configuration.GetSection(MonitoringOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services.AddOptions<LiveWatchlistOptions>()
    .Bind(builder.Configuration.GetSection(LiveWatchlistOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services.AddOptions<QuestradeOptions>()
    .Bind(builder.Configuration.GetSection(QuestradeOptions.SectionName))
    .ValidateDataAnnotations();

var questradeEnabled = builder.Configuration
    .GetSection(QuestradeOptions.SectionName)
    .GetValue<bool>(nameof(QuestradeOptions.Enabled));

// Both registered unconditionally: the watchlist can be curated with the live feed switched
// off. The cache is inert without something feeding it, and registering it always means the
// endpoints take a plain LiveQuoteCache — a minimal-API handler cannot bind an unregistered
// service parameter, even a nullable one.
builder.Services.AddScoped<WatchlistService>();
builder.Services.AddSingleton<LiveQuoteCache>();

var monitoringEnabled = builder.Configuration
    .GetSection(MonitoringOptions.SectionName)
    .GetValue<bool>(nameof(MonitoringOptions.Enabled));

// Price monitoring is opt-in: without it configured the app is exactly what it was before,
// and a developer running locally can never email anyone.
if (monitoringEnabled)
{
    builder.Services.AddOptions<MarketDataOptions>()
        .Bind(builder.Configuration.GetSection(MarketDataOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddOptions<SmtpOptions>()
        .Bind(builder.Configuration.GetSection(SmtpOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddHttpClient<IQuoteProvider, TwelveDataQuoteProvider>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<MarketDataOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        })
        .AddStandardResilienceHandler();

    builder.Services.AddSingleton<INotifier, SmtpNotifier>();
    builder.Services.AddScoped<PriceCheckJob>();
    builder.Services.AddHostedService<PriceCheckWorker>();
}

// Questrade is opt-in for the same reason monitoring is: a developer running locally should
// not open upstream connections or spend API calls by accident.
if (questradeEnabled)
{
    builder.Services.AddScoped<IQuestradeTokenStore, QuestradeTokenStore>();

    // Named, not typed: QuestradeAuthenticator is a singleton (it caches the live session
    // across the whole process), and AddHttpClient<TClient,TImplementation> always registers
    // the typed client as transient. A named client plus a hand-written singleton factory is
    // how a class needing an HttpClient stays a singleton. The 20s timeout is required, not
    // optional — RefreshAsync deliberately never propagates the caller's CancellationToken
    // past its semaphore (cancelling mid-exchange is how a single-use refresh token gets
    // lost), so HttpClient.Timeout is the only bound on a refresh, and the 100s default would
    // hold the single-flight gate closed that whole time.
    builder.Services.AddHttpClient("QuestradeAuth", client =>
        client.Timeout = TimeSpan.FromSeconds(20));
    builder.Services.AddSingleton<IQuestradeAuthenticator>(sp => new QuestradeAuthenticator(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("QuestradeAuth"),
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<IOptions<QuestradeOptions>>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<QuestradeAuthenticator>>()));

    // Same reasoning as the authenticator: the resolver caches resolved symbolIds for the
    // process lifetime, so it has to be a singleton too, which means the same named-client
    // workaround. No explicit timeout here — the default is fine for a symbol search.
    builder.Services.AddHttpClient("QuestradeSymbols");
    builder.Services.AddSingleton<IQuestradeSymbolResolver>(sp => new QuestradeSymbolResolver(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("QuestradeSymbols"),
        sp.GetRequiredService<IQuestradeAuthenticator>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<QuestradeSymbolResolver>>()));

    // The quote client has no per-tick state to cache, so the ordinary typed-client
    // registration (and its default lifetime) is fine. No BaseAddress: the base URL comes
    // from session.ApiServer at call time and changes between sessions.
    builder.Services.AddHttpClient<IQuestradeQuoteClient, QuestradeQuoteClient>();

    // Ordinary typed client: unlike the resolver this holds no per-process cache, so it has
    // no reason to be a singleton. It depends on the singleton resolver only to prime it.
    builder.Services.AddHttpClient<IQuestradeSymbolSearch, QuestradeSymbolSearch>();

    builder.Services.AddScoped<LiveWatchlistPollJob>();

    var liveWatchlistEnabled = builder.Configuration
        .GetSection(LiveWatchlistOptions.SectionName)
        .GetValue<bool>(nameof(LiveWatchlistOptions.Enabled));

    // Both flags, not just this one: Questrade can be connected with the poll worker off
    // (nothing to poll for yet), and the live watchlist can be curated with Questrade off.
    if (liveWatchlistEnabled)
    {
        builder.Services.AddHostedService<LiveWatchlistPollWorker>();
    }
}

// Without this, keys used to protect the auth cookie and antiforgery tokens live only in the
// container's writable layer and are regenerated on every restart, signing everyone out each time.
var dataProtectionKeysPath = builder.Configuration["DataProtectionKeysPath"];

// Data Protection resolves today even without the block below — Razor Pages pulls in
// antiforgery, which registers it transitively. That's a dependency nobody declared, and now
// that the encrypted refresh token rides on it too, a silent lockout on the next restart is
// not acceptable: fail fast instead, the same way Auth:AllowedEmail does.
if (questradeEnabled && string.IsNullOrEmpty(dataProtectionKeysPath))
{
    throw new InvalidOperationException(
        "Questrade:Enabled is true but DataProtectionKeysPath is not configured. Without "
        + "persisted Data Protection keys, the encrypted Questrade refresh token becomes "
        + "undecryptable on the next restart, silently locking out the connection. Set "
        + "DataProtectionKeysPath to a persistent directory before enabling Questrade.");
}

if (!string.IsNullOrEmpty(dataProtectionKeysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
        .SetApplicationName("StonkWatch");
}

// Single-user app: anyone can attempt Google sign-in, but only this one address is let in.
var allowedGoogleEmail = builder.Configuration["Auth:AllowedEmail"]
    ?? throw new InvalidOperationException(
        "Auth:AllowedEmail is not configured. Set the Google account allowed to sign in.");

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    })
    .AddGoogle(options =>
    {
        options.ClientId = builder.Configuration["Auth:Google:ClientId"]
            ?? throw new InvalidOperationException("Auth:Google:ClientId is not configured.");
        options.ClientSecret = builder.Configuration["Auth:Google:ClientSecret"]
            ?? throw new InvalidOperationException("Auth:Google:ClientSecret is not configured.");
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

        options.Events.OnCreatingTicket = context =>
        {
            var email = context.Identity?.FindFirst(ClaimTypes.Email)?.Value;
            if (!string.Equals(email, allowedGoogleEmail, StringComparison.OrdinalIgnoreCase))
            {
                context.Fail($"Google account '{email}' is not authorized for StonkWatch.");
            }

            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.Response.Redirect("/Account/Login?error=access_denied");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    })
    .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiKey", policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());

    // The sidebar is fetched by the browser with the session cookie, but the same routes
    // should stay usable from a script with a key. Both schemes, either one sufficient.
    options.AddPolicy("CookieOrApiKey", policy => policy
        .AddAuthenticationSchemes(
            CookieAuthenticationDefaults.AuthenticationScheme,
            ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());
});

var app = builder.Build();

// The container serves plain HTTP; TLS is expected to be terminated by a reverse proxy on the VPS.
// Trusting forwarded headers from any source is safe here because the container isn't meant to be
// reachable directly from the internet, only through that proxy.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Pin the UI culture. The host's locale would otherwise decide how prices render, and a
// quote shown as "302,53" in a US/Canada markets tool is a genuine misread risk. en-CA gives
// a decimal point and ISO-style dates.
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("en-CA")
    .AddSupportedCultures("en-CA")
    .AddSupportedUICultures("en-CA"));

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Anonymous by design: a monitor or the reverse proxy must be able to reach it. It reports
// liveness only and exposes no watchlist data.
app.MapHealthChecks("/healthz").AllowAnonymous();

app.MapRazorPages();
app.MapCandidateEndpoints();
app.MapAlertEndpoints();
app.MapWatchlistEndpoints();

// Registered only when the feature is on: with it off the routes must not exist at all,
// rather than exist and 500 on a service that was never registered.
if (questradeEnabled)
{
    app.MapQuestradeEndpoints();
}

app.Run();

// Exposed so the test project can host the app through WebApplicationFactory.
public partial class Program;
