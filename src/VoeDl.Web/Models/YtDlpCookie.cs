namespace VoeDl.Web.Models;

/// <summary>
/// Stores a user-provided yt-dlp cookies.txt file in the application database.
/// This is used to persist login/session cookies for YouTube and other protected hosts.
/// </summary>
public sealed class YtDlpCookie
{
    public int Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}
