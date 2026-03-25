using VoeDl.Web.Components;
using VoeDl.Web.Services;

var builder = WebApplication.CreateBuilder(args);

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

// Core application services
builder.Services.AddSingleton<DownloadService>();
builder.Services.AddSingleton<JobManagerService>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// HTTPS redirect is disabled by default in Docker; enable it only when a certificate is configured.
if (app.Configuration["ASPNETCORE_HTTPS_PORT"] is not null)
    app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
