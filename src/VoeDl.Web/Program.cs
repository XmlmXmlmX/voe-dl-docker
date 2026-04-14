using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using VoeDl.ServiceDefaults;
using VoeDl.Web.Components;
using VoeDl.Web.Data;
using VoeDl.Web.Services;
using System.IO;
using System.Runtime.InteropServices;

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

// Dedicated client for MediathekViewWeb RSS search.
builder.Services.AddHttpClient("mediathek", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
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
builder.Services.AddSingleton<MediathekViewWebService>();
builder.Services.AddSingleton<DownloadService>();
builder.Services.AddSingleton<JobManagerService>();
builder.Services.AddSingleton<IJobManagerService>(sp => sp.GetRequiredService<JobManagerService>());

// Persist data protection keys to a configurable path so antiforgery tokens
// remain decryptable across container restarts and updates.
var dataProtectionBuilder = builder.Services
    .AddDataProtection()
    .SetApplicationName("VoeDl.Web");

var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    CreateDirectoryWithUnixPermissions(dataProtectionKeysPath);
    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

// Antiforgery cookie configuration:
// - IsEssential = false: don't block if cookie consent middleware is present
// - SameSite = Strict: no cross-site requests
// - MaxAge = 1h: short lifetime so stale cookies (from a container restart with
//   different Data Protection keys) expire quickly and don't keep the Blazor circuit
//   from connecting (the symptom is: buttons do nothing, blazor.web.js errors in console).
//   Without MaxAge, browsers with session-restore keep the cookie alive indefinitely.
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.MaxAge = TimeSpan.FromHours(1);
    options.Cookie.SameSite = SameSiteMode.Strict;
});

// Add services to the container.
builder.Services.AddMudServices();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Apply database schema if PostgreSQL is configured
if (!string.IsNullOrWhiteSpace(pgConnectionString))
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();
        await ctx.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to apply database migrations. The app will fall back to file-based history storage. Ensure PostgreSQL is accessible and configured correctly.");
    }
}

// Configure the HTTP request pipeline.
// ForwardedHeaders must be applied first so that all subsequent middleware
// (including exception handler, HSTS, HTTPS redirect, antiforgery) sees the
// real scheme/host that the end user's browser used.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // Only send HSTS headers when HTTPS is actually configured.
    // Sending Strict-Transport-Security over plain HTTP causes browsers to cache
    // the policy and refuse future HTTP connections (breaking HTTP-only Docker
    // deployments such as TrueNAS Scale Apps).
    if (!string.IsNullOrEmpty(app.Configuration["ASPNETCORE_HTTPS_PORT"]) ||
        !string.IsNullOrEmpty(app.Configuration["ASPNETCORE_HTTPS_PORTS"]))
        app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// HTTPS redirect is disabled by default in Docker; enable it only when a certificate is configured.
if (!string.IsNullOrEmpty(app.Configuration["ASPNETCORE_HTTPS_PORT"]))
    app.UseHttpsRedirection();

// Fallback for environments where endpoint-based static asset mapping is not available.
app.UseStaticFiles();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

// Log startup diagnostics
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation("=== VoeDl Application Startup ===");
startupLogger.LogInformation("Database connection configured: {DbConfigured}", !string.IsNullOrWhiteSpace(pgConnectionString));
startupLogger.LogInformation("Environment: {Environment}", app.Environment.EnvironmentName);

// Diagnose environment variable configuration for troubleshooting
startupLogger.LogInformation("--- Environment Variable Diagnostics ---");
var downloadDir = app.Configuration["DOWNLOAD_DIR"] ?? "(not set)";
var downloadPath = app.Configuration["DOWNLOAD_PATH"] ?? "(not set)";
var dataProtectionPath = app.Configuration["DataProtection:KeysPath"] ?? "(not set)";
startupLogger.LogInformation("DOWNLOAD_DIR env: {DownloadDir}", downloadDir);
startupLogger.LogInformation("DOWNLOAD_PATH env: {DownloadPath}", downloadPath);
startupLogger.LogInformation("DataProtection:KeysPath env: {DataProtectionPath}", dataProtectionPath);
startupLogger.LogInformation("ConnectionStrings:DefaultConnection: {DbConnectionString}", 
    string.IsNullOrWhiteSpace(pgConnectionString) ? "(not set)" : "(configured)");
startupLogger.LogInformation("--- End Diagnostics ---");

startupLogger.LogInformation("Application started successfully. Listening on http://[::]:8080");

app.Run();

// Helper functions for Unix file permissions
void CreateDirectoryWithUnixPermissions(string path)
{
    Directory.CreateDirectory(path);
    SetUnixFilePermissions(path);
}

void SetUnixFilePermissions(string path)
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        try
        {
            // Set permissions to 0o770 (rwxrwx---) for user and group
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Could not set Unix file mode for {path}: {ex.Message}");
        }
    }
}
