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
using StonkWatch.Web.Services.Monitoring;
using StonkWatch.Web.Services.Notifications;

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

// Without this, keys used to protect the auth cookie and antiforgery tokens live only in the
// container's writable layer and are regenerated on every restart, signing everyone out each time.
var dataProtectionKeysPath = builder.Configuration["DataProtectionKeysPath"];
if (!string.IsNullOrEmpty(dataProtectionKeysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
        .SetApplicationName("StonkWatch");
}

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

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
app.MapMcp("/mcp").RequireAuthorization("ApiKey");

app.Run();
