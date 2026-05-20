using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VoeDl.Web.Data;

/// <summary>
/// Design-time factory used by dotnet-ef migrations.
/// Reads the connection string from the <c>ConnectionStrings__DefaultConnection</c>
/// environment variable so that no credentials need to be embedded in source code.
/// </summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings:DefaultConnection")
            ?? Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? Environment.GetEnvironmentVariable("DATABASE_URI")
            ?? BuildConnectionStringFromPostgresEnvironment()
            ?? "Host=localhost;Database=voedl;Username=voedl;Password=voedl_pass";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr)
            .Options;
        return new AppDbContext(options);
    }

    private static string? BuildConnectionStringFromPostgresEnvironment()
    {
        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST")
            ?? Environment.GetEnvironmentVariable("PGHOST");
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var database = Environment.GetEnvironmentVariable("POSTGRES_DB")
            ?? Environment.GetEnvironmentVariable("PGDATABASE")
            ?? "voedl";
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER")
            ?? Environment.GetEnvironmentVariable("PGUSER")
            ?? "voedl";
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? Environment.GetEnvironmentVariable("PGPASSWORD");
        if (string.IsNullOrWhiteSpace(password))
            return null;

        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = host,
            Database = database,
            Username = user,
            Password = password
        };

        if (int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT")
            ?? Environment.GetEnvironmentVariable("PGPORT"), out var port))
        {
            builder.Port = port;
        }

        return builder.ToString();
    }
}
