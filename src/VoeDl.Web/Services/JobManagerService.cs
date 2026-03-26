using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VoeDl.Web.Data;
using VoeDl.Web.Models;

namespace VoeDl.Web.Services;

/// <summary>
/// In-memory job manager that mirrors the job lifecycle implemented in
/// the original app.py (pending → running → done/error/aborted).
/// Thread-safe for concurrent Blazor component access.
/// </summary>
public sealed class JobManagerService
{
    private static readonly Regex DownloadedWithTotalRegex = new(
        @"\[\*\] Downloaded\s+(?<current>[\d\.]+)\s+MB\s+/\s+(?<total>[\d\.]+)\s+MB",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex YtDlpPercentRegex = new(
        @"\[download\]\s+(?<percent>[\d\.]+)%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();

    private readonly DownloadService _downloader;
    private readonly ILogger<JobManagerService> _logger;
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;

    private readonly string _downloadDir;
    private readonly string _historyFile;
    private readonly SemaphoreSlim _historyLock = new(1, 1);

    // Raised whenever any job changes state so Blazor components can re-render
    public event Action? JobsChanged;

    public JobManagerService(
        DownloadService downloadService,
        ILogger<JobManagerService> logger,
        IConfiguration configuration,
        IServiceProvider services)
    {
        _downloader = downloadService;
        _logger = logger;
        _dbFactory = services.GetService<IDbContextFactory<AppDbContext>>();

        string rawDir = configuration["DOWNLOAD_DIR"]
                       ?? configuration["DOWNLOAD_PATH"]
                       ?? Environment.GetEnvironmentVariable("DOWNLOAD_DIR")
                       ?? Environment.GetEnvironmentVariable("DOWNLOAD_PATH")
                       ?? "/downloads";

        _downloadDir = Path.IsPathRooted(rawDir) ? rawDir : Path.GetFullPath(rawDir);
        Directory.CreateDirectory(_downloadDir);
        _historyFile = Path.Combine(_downloadDir, "downloaded_urls.txt");
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>Returns all jobs ordered newest-first.</summary>
    public IReadOnlyList<DownloadJob> GetJobs() =>
        _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

    /// <summary>Returns a single job or null.</summary>
    public DownloadJob? GetJob(string id) => _jobs.GetValueOrDefault(id);

    /// <summary>
    /// Enqueues a new download job for <paramref name="url"/> and starts it
    /// immediately on a background thread.
    /// </summary>
    public DownloadJob Enqueue(string url)
    {
        var job = new DownloadJob { Url = url };
        _jobs[job.Id] = job;
        NotifyChanged();

        _ = Task.Run(() => RunJobAsync(job));
        return job;
    }

    /// <summary>Requests cancellation of a running or pending job.</summary>
    public bool Abort(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        if (job.Status is not (JobStatus.Pending or JobStatus.Running)) return false;

        job.Status = JobStatus.Aborted;
        job.FinishedAt = DateTimeOffset.UtcNow;

        if (_cancellations.TryRemove(id, out var cts))
        {
            try { cts.Cancel(); cts.Dispose(); }
            catch { /* ignore */ }
        }

        NotifyChanged();
        return true;
    }

    /// <summary>Creates a new job for the same URL as an existing finished job.</summary>
    public DownloadJob? Requeue(string id)
    {
        if (!_jobs.TryGetValue(id, out var existing)) return null;
        if (existing.Status is not (JobStatus.Done or JobStatus.Error or JobStatus.Aborted))
            return null;

        return Enqueue(existing.Url);
    }

    /// <summary>Returns all URLs that were successfully downloaded (chronological).</summary>
    public async Task<IReadOnlyList<string>> LoadHistoryAsync()
    {
        if (_dbFactory is not null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.DownloadHistory
                .OrderBy(e => e.FinishedAt)
                .Select(e => e.Url)
                .ToListAsync();
        }

        var urls = new List<string>();
        await _historyLock.WaitAsync();
        try
        {
            if (!File.Exists(_historyFile)) return urls;
            foreach (var line in await File.ReadAllLinesAsync(_historyFile))
            {
                string trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith('#'))
                    urls.Add(trimmed);
            }
        }
        finally { _historyLock.Release(); }
        return urls;
    }

    /// <summary>
    /// Returns full history entries ordered newest-first.
    /// Only available when PostgreSQL is configured; returns an empty list otherwise.
    /// </summary>
    public async Task<IReadOnlyList<DownloadHistoryEntry>> LoadHistoryEntriesAsync()
    {
        if (_dbFactory is null) return [];
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.DownloadHistory
            .OrderByDescending(e => e.FinishedAt)
            .ToListAsync();
    }

    // ------------------------------------------------------------------
    // Internal
    // ------------------------------------------------------------------

    private async Task RunJobAsync(DownloadJob job)
    {
        // If the job was aborted before this task started, exit immediately
        if (job.Status == JobStatus.Aborted)
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
            NotifyChanged();
            return;
        }

        var cts = new CancellationTokenSource();
        _cancellations[job.Id] = cts;

        int timeoutSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("DOWNLOAD_TIMEOUT"), out int t) ? t : 3600;

        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        job.Status = JobStatus.Running;
        job.Phase = JobPhase.Preparing;
        job.ProgressPercent = null;
        job.StartedAt = DateTimeOffset.UtcNow;
        NotifyChanged();

        try
        {
            int exitCode = await _downloader.DownloadAsync(
                job.Url,
                _downloadDir,
                line =>
                {
                    job.Logs += line + "\n";
                    UpdateJobProgressFromLog(job, line);
                    // Extract title from log lines (matching app.py behaviour)
                    if (line.StartsWith("Name of file: "))
                        job.Title = line["Name of file: ".Length..];
                    else if (line.StartsWith("Using default file name: "))
                        job.Title = line["Using default file name: ".Length..];
                    NotifyChanged();
                },
                cts.Token);

            if (job.Status != JobStatus.Aborted)
            {
                job.ReturnCode = exitCode;
                job.Status = exitCode == 0 ? JobStatus.Done : JobStatus.Error;
                job.ProgressPercent = exitCode == 0 ? 100 : job.ProgressPercent;
            }

            if (exitCode == 0)
                await AppendToHistoryAsync(job);
        }
        catch (OperationCanceledException)
        {
            if (job.Status != JobStatus.Aborted)
            {
                job.Status = JobStatus.Error;
                job.Logs += $"\nDownload timed out after {timeoutSeconds} seconds.";
            }
        }
        catch (Exception ex)
        {
            if (job.Status != JobStatus.Aborted)
            {
                job.Status = JobStatus.Error;
                job.Logs += "\n" + ex.Message;
            }
            _logger.LogError(ex, "Unhandled error in job {JobId}", job.Id);
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
            _cancellations.TryRemove(job.Id, out var removed);
            try { removed?.Dispose(); } catch { /* ignore */ }
            NotifyChanged();
        }
    }

    private async Task AppendToHistoryAsync(DownloadJob job)
    {
        if (_dbFactory is not null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.DownloadHistory.Add(new DownloadHistoryEntry
            {
                Id = job.Id,
                Url = job.Url,
                Title = job.Title,
                CreatedAt = job.CreatedAt,
                FinishedAt = job.FinishedAt ?? DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return;
        }

        await _historyLock.WaitAsync();
        try { await File.AppendAllTextAsync(_historyFile, job.Url + "\n"); }
        finally { _historyLock.Release(); }
    }

    private static void UpdateJobProgressFromLog(DownloadJob job, string line)
    {
        if (line.StartsWith("[*] Downloading ", StringComparison.Ordinal))
        {
            job.Phase = JobPhase.Downloading;
            return;
        }

        var downloadedWithTotalMatch = DownloadedWithTotalRegex.Match(line);
        if (downloadedWithTotalMatch.Success)
        {
            if (double.TryParse(downloadedWithTotalMatch.Groups["current"].Value, out var currentMb)
                && double.TryParse(downloadedWithTotalMatch.Groups["total"].Value, out var totalMb)
                && totalMb > 0)
            {
                job.Phase = JobPhase.Downloading;
                job.ProgressPercent = Math.Clamp(currentMb / totalMb * 100.0, 0, 100);
            }
            return;
        }

        var ytDlpPercentMatch = YtDlpPercentRegex.Match(line);
        if (ytDlpPercentMatch.Success
            && double.TryParse(ytDlpPercentMatch.Groups["percent"].Value, out var ytPercent))
        {
            job.Phase = JobPhase.Downloading;
            job.ProgressPercent = Math.Clamp(ytPercent, 0, 100);
        }
    }

    private void NotifyChanged() => JobsChanged?.Invoke();
}
