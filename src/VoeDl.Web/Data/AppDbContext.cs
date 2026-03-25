using Microsoft.EntityFrameworkCore;
using VoeDl.Web.Models;

namespace VoeDl.Web.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DownloadHistoryEntry> DownloadHistory => Set<DownloadHistoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DownloadHistoryEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Url).IsRequired();
            entity.HasIndex(e => e.Url);
            entity.HasIndex(e => e.FinishedAt);
        });
    }
}
