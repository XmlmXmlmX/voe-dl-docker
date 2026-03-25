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
            ?? "Host=localhost;Database=voedl;Username=voedl;Password=voedl_pass";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr)
            .Options;
        return new AppDbContext(options);
    }
}
