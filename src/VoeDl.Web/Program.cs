using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using VoeDl.ServiceDefaults;
using VoeDl.Web.Components;
using VoeDl.Web.Data;
using VoeDl.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Trust forwarded headers from any upstream proxy (Docker / TrueNAS Scale / Traefik).
// Without this the app has no knowledge of the public-facing scheme and host, which
// causes ASP.NET Core's antiforgery validation to reject requests (HTTP 400) because
// the Origin header set by the browser does not match the container-internal Host.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;
    // Clear the default IP-whitelist so any upstream proxy is accepted.
    // This is intentional for Docker deployments: the container is never
    // directly reachable from the internet; traffic always arrives via the
    // Docker bridge / host network that is already controlled by the
    // orchestrator (TrueNAS Scale, Portainer, plain Docker, etc.).
    // If you expose the container port directly to an untrusted network
    // without a reverse proxy, remove these two lines and instead add
    // only the specific trusted proxy IP(s) to KnownIPNetworks.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Bind configuration from environment variables (matches docker-compose)
builder.Configuration.AddEnvironmentVariables();

// HTTP client used by the download service – no automatic redirect following
// so that we can handle JS-based redirects ourselves.
builder.Services.AddHttpClient("voe", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Dedicated client for streaming large media files; timeout is effectively
// infinite so that we rely solely on the CancellationToken for cancellation.
builder.Services.AddHttpClient("voe-download", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});

// TMDB API client – Bearer token is read from configuration.
// If TheMovieDbApiAccessToken is empty/absent, TMDB lookups are skipped.
builder.Services.AddHttpClient("tmdb", (sp, client) =>
{
    var token = sp.GetRequiredService<IConfiguration>()["TheMovieDbApiAccessToken"] ?? "";
    client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
    client.Timeout = TimeSpan.FromSeconds(15);
    if (!string.IsNullOrWhiteSpace(token))
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
});

// Core application services
var pgConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrWhiteSpace(pgConnectionString))
{
    builder.Services.AddDbContextFactory<AppDbContext>(options =>
        options.UseNpgsql(pgConnectionString));
}

builder.Services.AddSingleton<TmdbService>();
builder.Services.AddSingleton<DownloadService>();
builder.Services.AddSingleton<JobManagerService>();
builder.Services.AddSingleton<IJobManagerService>(sp => sp.GetRequiredService<JobManagerService>());

// Add services to the container.
builder.Services.AddMudServices();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Apply database schema if PostgreSQL is configured
if (!string.IsNullOrWhiteSpace(pgConnectionString))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var ctx = await db.CreateDbContextAsync();
    await ctx.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
// ForwardedHeaders must be applied first so that all subsequent middleware
// (including exception handler, HSTS, HTTPS redirect, antiforgery) sees the
// real scheme/host that the end user's browser used.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// HTTPS redirect is disabled by default in Docker; enable it only when a certificate is configured.
if (app.Configuration["ASPNETCORE_HTTPS_PORT"] is not null)
    app.UseHttpsRedirection();

// Fallback for environments where endpoint-based static asset mapping is not available.
app.UseStaticFiles();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
