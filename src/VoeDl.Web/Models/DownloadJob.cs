namespace VoeDl.Web.Models;

public enum JobStatus
{
    Pending,
    Running,
    Done,
    Error,
    Aborted
}

public enum JobPhase
{
    Preparing,
    Downloading
}

public sealed class DownloadJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Url { get; init; } = string.Empty;
    public string? Title { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Logs { get; set; } = string.Empty;
    public int? ReturnCode { get; set; }
    public JobPhase Phase { get; set; } = JobPhase.Preparing;
    public double? ProgressPercent { get; set; }
}
