using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = DistributedApplication.CreateBuilder(args);

var postgresUserValue = builder.Configuration["POSTGRES_USER"] ?? "voedl";
var postgresPasswordValue = builder.Configuration["POSTGRES_PASSWORD"] ?? "voedl_pass";
var postgresDatabase = builder.Configuration["POSTGRES_DB"] ?? "voedl";

var postgresUser = builder.AddParameter(
	"postgres-username",
	postgresUserValue,
	publishValueAsDefault: true,
	secret: false);

var postgresPassword = builder.AddParameter(
	"postgres-password",
	postgresPasswordValue,
	publishValueAsDefault: false,
	secret: true);

var postgres = builder.AddPostgres("postgres", userName: postgresUser, password: postgresPassword)
	.WithDataVolume("voedl-postgres-data");

var appDb = postgres.AddDatabase("DefaultConnection", postgresDatabase);

var webProjectPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "VoeDl.Web"));
builder.Configuration
	.AddJsonFile(Path.Combine(webProjectPath, "appsettings.json"), optional: true, reloadOnChange: false)
	.AddJsonFile(Path.Combine(webProjectPath, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true);

if (string.IsNullOrWhiteSpace(builder.Configuration["TheMovieDbApiAccessToken"]))
{
	builder.Configuration.AddJsonFile(Path.Combine(webProjectPath, "appsettings.Development.json"), optional: true, reloadOnChange: false);
}

var tmdbToken = builder.Configuration["TheMovieDbApiAccessToken"] ?? "";
var tmdbLanguage = builder.Configuration["TheMovieDbLanguage"] ?? "en-US";

// Aspire spawns the web project in an isolated process, so PATH from the
// developer's shell is not reliably inherited. Resolve yt-dlp here and
// forward the full path so the web process always finds it.
var ytDlpPath =  builder.Configuration["YtDlpPath"] ?? "";
if (string.IsNullOrWhiteSpace(ytDlpPath))
{
    var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
    string[] candidates = OperatingSystem.IsWindows()
        ? ["yt-dlp.exe", "yt-dlp"]
        : ["yt-dlp"];
    ytDlpPath = pathDirs
        .SelectMany(dir => candidates.Select(bin => Path.Combine(dir, bin)))
        .FirstOrDefault(File.Exists) ?? "";
}

builder.AddProject("voedl-web", "../VoeDl.Web/VoeDl.Web.csproj")
	.WithReference(appDb)
	.WaitFor(appDb)
	.WithEnvironment("TheMovieDbApiAccessToken", tmdbToken)
	.WithEnvironment("TheMovieDbLanguage", tmdbLanguage)
	.WithEnvironment("YT_DLP_PATH", ytDlpPath);

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VoeDl.AppHost");

if (string.IsNullOrWhiteSpace(tmdbToken))
{
	logger.LogWarning("TheMovieDbApiAccessToken fehlt oder ist leer. TMDB-Metadaten sind nicht verfügbar.");
}
else
{
	logger.LogInformation("TheMovieDbApiAccessToken wurde geladen.");
}

logger.LogInformation("TheMovieDbLanguage: {Language}", tmdbLanguage);

if (string.IsNullOrWhiteSpace(ytDlpPath))
{
	logger.LogWarning("yt-dlp wurde nicht gefunden. YT_DLP_PATH setzen oder yt-dlp im PATH installieren.");
}
else
{
	logger.LogInformation("yt-dlp gefunden: {Path}", ytDlpPath);
}

app.Run();
