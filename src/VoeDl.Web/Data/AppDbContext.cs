using Microsoft.EntityFrameworkCore;
using VoeDl.Web.Models;

namespace VoeDl.Web.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DownloadHistoryEntry> DownloadHistory => Set<DownloadHistoryEntry>();
    public DbSet<YtDlpCookie> YtDlpCookies => Set<YtDlpCookie>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DownloadHistoryEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Url).IsRequired();
            entity.HasIndex(e => e.Url);
            entity.HasIndex(e => e.FinishedAt);
        });

        modelBuilder.Entity<YtDlpCookie>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.UploadedAt).IsRequired();
        });
    }
}
