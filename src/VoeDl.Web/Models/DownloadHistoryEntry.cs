namespace VoeDl.Web.Models;

/// <summary>
/// Persistent record of a completed download, stored in PostgreSQL.
/// </summary>
public sealed class DownloadHistoryEntry
{
    public string Id { get; set; } = default!;
    public string Url { get; set; } = default!;
    public string? Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool Success { get; set; } = true;
}
