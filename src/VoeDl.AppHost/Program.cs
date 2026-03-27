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
	.AddJsonFile(Path.Combine(webProjectPath, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: false);

if (string.IsNullOrWhiteSpace(builder.Configuration["TheMovieDbApiAccessToken"]))
{
	builder.Configuration.AddJsonFile(Path.Combine(webProjectPath, "appsettings.Development.json"), optional: true, reloadOnChange: false);
}

var tmdbToken = builder.Configuration["TheMovieDbApiAccessToken"] ?? "";
var tmdbLanguage = builder.Configuration["TheMovieDbLanguage"] ?? "en-US";

builder.AddProject("voedl-web", "../VoeDl.Web/VoeDl.Web.csproj")
	.WithReference(appDb)
	.WaitFor(appDb)
	.WithEnvironment("TheMovieDbApiAccessToken", tmdbToken)
	.WithEnvironment("TheMovieDbLanguage", tmdbLanguage);

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VoeDl.AppHost");

if (string.IsNullOrWhiteSpace(tmdbToken))
{
	logger.LogWarning("TheMovieDbApiAccessToken fehlt oder ist leer. TMDB-Metadaten sind nicht verfugbar.");
}
else
{
	logger.LogInformation("TheMovieDbApiAccessToken wurde geladen.");
}

logger.LogInformation("TheMovieDbLanguage: {Language}", tmdbLanguage);

app.Run();
