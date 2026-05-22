using System.Text;
using Microsoft.EntityFrameworkCore;
using VoeDl.Web.Data;
using VoeDl.Web.Models;

namespace VoeDl.Web.Services;

public sealed class CookieStoreService
{
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly ILogger<CookieStoreService> _logger;

    public CookieStoreService(IDbContextFactory<AppDbContext>? dbFactory, ILogger<CookieStoreService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public bool IsDatabaseConfigured => _dbFactory is not null;

    public async Task<bool> HasStoredCookieAsync()
    {
        return await GetLatestCookieAsync() is not null;
    }

    public async Task<DateTimeOffset?> GetCookieUploadedAtAsync()
    {
        var cookie = await GetLatestCookieAsync();
        return cookie?.UploadedAt;
    }

    public async Task SaveCookieAsync(string content)
    {
        if (_dbFactory is null)
            throw new InvalidOperationException("Database is not configured for yt-dlp cookie storage.");

        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.YtDlpCookies.ExecuteDeleteAsync();
        db.YtDlpCookies.Add(new YtDlpCookie
        {
            Content = content,
            UploadedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    public async Task DeleteCookieAsync()
    {
        if (_dbFactory is null)
            return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.YtDlpCookies.RemoveRange(db.YtDlpCookies);
        await db.SaveChangesAsync();
    }

    public async Task<string?> CreateTempCookieFileAsync()
    {
        var cookie = await GetLatestCookieAsync();
        if (cookie is null)
            return null;

        var tempFile = Path.Combine(Path.GetTempPath(), $"voedl-yt-dlp-cookies-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, cookie.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return tempFile;
    }

    private async Task<YtDlpCookie?> GetLatestCookieAsync()
    {
        if (_dbFactory is null)
            return null;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.YtDlpCookies
                .OrderByDescending(c => c.UploadedAt)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read yt-dlp cookie from database.");
            return null;
        }
    }
}
