using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VoeDl.Web.Data;
using VoeDl.Web.Models;

namespace VoeDl.Web.Services;

/// <summary>
/// In-memory job manager that mirrors the job lifecycle implemented in
/// the original app.py (queued → running → done/error/aborted).
/// Thread-safe for concurrent Blazor component access.
/// </summary>
public sealed class JobManagerService : IJobManagerService
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
    private readonly string _seriesDownloadDir;
    private readonly string _documentaryDownloadDir;
    private readonly string _historyFile;
    private readonly SemaphoreSlim _historyLock = new(1, 1);
    private readonly SemaphoreSlim _downloadSlots;
    private readonly int _maxConcurrentDownloads;

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

        string rawDir = FirstMeaningfulDirectoryValue(
            configuration["DOWNLOAD_DIR"],
            configuration["DOWNLOAD_PATH"],
            Environment.GetEnvironmentVariable("DOWNLOAD_DIR"),
            Environment.GetEnvironmentVariable("DOWNLOAD_PATH"))
            ?? "/downloads";

        string? rawSeriesDir = FirstMeaningfulDirectoryValue(
            configuration["DOWNLOAD_DIR_SERIES"],
            configuration["SERIES_DOWNLOAD_DIR"],
            configuration["TV_DOWNLOAD_DIR"],
            configuration["DOWNLOAD_DIR_TV"],
            Environment.GetEnvironmentVariable("DOWNLOAD_DIR_SERIES"),
            Environment.GetEnvironmentVariable("SERIES_DOWNLOAD_DIR"),
            Environment.GetEnvironmentVariable("TV_DOWNLOAD_DIR"),
            Environment.GetEnvironmentVariable("DOWNLOAD_DIR_TV"));
        
        string? rawDocumentaryDir = FirstMeaningfulDirectoryValue(
            configuration["DOCUMENTARY_DOWNLOAD_DIR"],
            configuration["DOWNLOAD_DIR_DOCUMENTARY"],
            Environment.GetEnvironmentVariable("DOCUMENTARY_DOWNLOAD_DIR"),
            Environment.GetEnvironmentVariable("DOWNLOAD_DIR_DOCUMENTARY"));
        
        _logger.LogInformation("Download directory resolution (first match wins):");
        _logger.LogInformation("  Config[DOWNLOAD_DIR] = {ConfigDownloadDir}", configuration["DOWNLOAD_DIR"] ?? "(null)");
        _logger.LogInformation("  Config[DOWNLOAD_PATH] = {ConfigDownloadPath}", configuration["DOWNLOAD_PATH"] ?? "(null)");
        _logger.LogInformation("  Env[DOWNLOAD_DIR] = {EnvDownloadDir}", Environment.GetEnvironmentVariable("DOWNLOAD_DIR") ?? "(null)");
        _logger.LogInformation("  Env[DOWNLOAD_PATH] = {EnvDownloadPath}", Environment.GetEnvironmentVariable("DOWNLOAD_PATH") ?? "(null)");
        _logger.LogInformation("  => Using: {ResolvedDir}", rawDir);
        _logger.LogInformation("  Config[DOWNLOAD_DIR_SERIES] = {ConfigSeriesDir}", configuration["DOWNLOAD_DIR_SERIES"] ?? "(null)");
        _logger.LogInformation("  Config[SERIES_DOWNLOAD_DIR] = {ConfigSeriesDirLegacy}", configuration["SERIES_DOWNLOAD_DIR"] ?? "(null)");
        _logger.LogInformation("  Config[TV_DOWNLOAD_DIR] = {ConfigTvDownloadDir}", configuration["TV_DOWNLOAD_DIR"] ?? "(null)");
        _logger.LogInformation("  Config[DOWNLOAD_DIR_TV] = {ConfigDownloadDirTv}", configuration["DOWNLOAD_DIR_TV"] ?? "(null)");
        _logger.LogInformation("  Env[DOWNLOAD_DIR_SERIES] = {EnvSeriesDir}", Environment.GetEnvironmentVariable("DOWNLOAD_DIR_SERIES") ?? "(null)");
        _logger.LogInformation("  Env[SERIES_DOWNLOAD_DIR] = {EnvSeriesDirLegacy}", Environment.GetEnvironmentVariable("SERIES_DOWNLOAD_DIR") ?? "(null)");
        _logger.LogInformation("  Env[TV_DOWNLOAD_DIR] = {EnvTvDownloadDir}", Environment.GetEnvironmentVariable("TV_DOWNLOAD_DIR") ?? "(null)");
        _logger.LogInformation("  Env[DOWNLOAD_DIR_TV] = {EnvDownloadDirTv}", Environment.GetEnvironmentVariable("DOWNLOAD_DIR_TV") ?? "(null)");
        _logger.LogInformation("  => Using series dir: {ResolvedSeriesDir}", rawSeriesDir ?? rawDir);
        _logger.LogInformation("  Config[DOCUMENTARY_DOWNLOAD_DIR] = {ConfigDocumentaryDir}", configuration["DOCUMENTARY_DOWNLOAD_DIR"] ?? "(null)");
        _logger.LogInformation("  Config[DOWNLOAD_DIR_DOCUMENTARY] = {ConfigDownloadDirDocumentary}", configuration["DOWNLOAD_DIR_DOCUMENTARY"] ?? "(null)");
        _logger.LogInformation("  Env[DOCUMENTARY_DOWNLOAD_DIR] = {EnvDocumentaryDir}", Environment.GetEnvironmentVariable("DOCUMENTARY_DOWNLOAD_DIR") ?? "(null)");
        _logger.LogInformation("  Env[DOWNLOAD_DIR_DOCUMENTARY] = {EnvDownloadDirDocumentary}", Environment.GetEnvironmentVariable("DOWNLOAD_DIR_DOCUMENTARY") ?? "(null)");
        _logger.LogInformation("  => Using documentary dir: {ResolvedDocumentaryDir}", rawDocumentaryDir ?? rawDir);

        _maxConcurrentDownloads = ParseMaxConcurrentDownloads(configuration);
        _downloadSlots = new SemaphoreSlim(_maxConcurrentDownloads, _maxConcurrentDownloads);
        _logger.LogInformation("Max concurrent downloads configured to {MaxConcurrentDownloads}", _maxConcurrentDownloads);
        _logger.LogInformation("Database factory available: {DbFactoryAvailable}", _dbFactory is not null);
        if (_dbFactory is null)
            _logger.LogWarning("PostgreSQL not configured. Download history will be stored in file.");

        _downloadDir = Path.IsPathRooted(rawDir) ? rawDir : Path.GetFullPath(rawDir);
        _seriesDownloadDir = string.IsNullOrWhiteSpace(rawSeriesDir)
            ? _downloadDir
            : (Path.IsPathRooted(rawSeriesDir) ? rawSeriesDir : Path.GetFullPath(rawSeriesDir));
        _documentaryDownloadDir = string.IsNullOrWhiteSpace(rawDocumentaryDir)
            ? _downloadDir
            : (Path.IsPathRooted(rawDocumentaryDir) ? rawDocumentaryDir : Path.GetFullPath(rawDocumentaryDir));
        _logger.LogInformation("Final download directory (absolute path): {FinalDownloadDir}", _downloadDir);
        _logger.LogInformation("Final series directory (absolute path): {FinalSeriesDownloadDir}", _seriesDownloadDir);
        _logger.LogInformation("Final documentary directory (absolute path): {FinalDocumentaryDownloadDir}", _documentaryDownloadDir);
        CreateDirectoryWithUnixPermissions(_downloadDir);
        CreateDirectoryWithUnixPermissions(_seriesDownloadDir);
        CreateDirectoryWithUnixPermissions(_documentaryDownloadDir);
        _historyFile = Path.Combine(_downloadDir, "downloaded_urls.txt");
        _logger.LogInformation("Download history file: {HistoryFile}", _historyFile);
    }

    private static string? FirstMeaningfulDirectoryValue(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalized = NormalizeDirectoryCandidate(candidate);
            if (normalized is not null)
                return normalized;
        }

        return null;
    }

    private static string? NormalizeDirectoryCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();

        // Ignore unresolved placeholders often entered in NAS UIs by mistake.
        if (trimmed.StartsWith("${", StringComparison.Ordinal) ||
            trimmed.StartsWith("$", StringComparison.Ordinal) ||
            trimmed.Equals("DOWNLOAD_DIR", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("/DOWNLOAD_DIR", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("DOWNLOAD_PATH", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("/DOWNLOAD_PATH", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("DOWNLOAD_DIR_SERIES", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("/DOWNLOAD_DIR_SERIES", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("SERIES_DOWNLOAD_DIR", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("/SERIES_DOWNLOAD_DIR", StringComparison.OrdinalIgnoreCase))
            return null;

        return trimmed;
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
    public DownloadJob Enqueue(string url, string? title = null, DownloadCategory category = DownloadCategory.Auto)
    {
        var job = new DownloadJob { Url = url, Title = title, Category = category };
        _jobs[job.Id] = job;
        _logger.LogInformation("Enqueued download job {JobId} for URL {Url} (Title: {Title}, Category: {Category})", job.Id, url, title ?? "(none)", category);
        NotifyChanged();

        _ = Task.Run(() => RunJobAsync(job));
        return job;
    }

    /// <summary>
    /// Expands known series URLs (for example s.to root URLs) into episode URLs
    /// and enqueues one job per resulting URL.
    /// </summary>
    public async Task<IReadOnlyList<DownloadJob>> EnqueueResolvedAsync(string inputUrl, CancellationToken cancellationToken = default)
    {
        var resolvedUrls = await _downloader.ExpandInputUrlAsync(inputUrl, null, cancellationToken);
        var jobs = new List<DownloadJob>(resolvedUrls.Count);
        foreach (var resolvedUrl in resolvedUrls)
            jobs.Add(Enqueue(resolvedUrl));
        return jobs;
    }

    /// <summary>
    /// Resolves a user input URL to one or many concrete download URLs.
    /// </summary>
    public Task<IReadOnlyList<string>> ResolveInputUrlsAsync(string inputUrl, CancellationToken cancellationToken = default) =>
        _downloader.ExpandInputUrlAsync(inputUrl, null, cancellationToken);

    /// <summary>Requests cancellation of a running or queued job.</summary>
    public bool Abort(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        if (job.Status is not (JobStatus.Queued or JobStatus.Running)) return false;

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
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                return await db.DownloadHistory
                    .OrderBy(e => e.FinishedAt)
                    .Select(e => e.Url)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load history from PostgreSQL database, falling back to file-based history");
            }
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load history from file {HistoryFile}", _historyFile);
        }
        finally { _historyLock.Release(); }
        return urls;
    }

    /// <summary>
    /// Returns full history entries ordered newest-first.
    /// Only available when PostgreSQL is configured and accessible; returns an empty list otherwise.
    /// </summary>
    public async Task<IReadOnlyList<DownloadHistoryEntry>> LoadHistoryEntriesAsync()
    {
        if (_dbFactory is null)
        {
            _logger.LogDebug("PostgreSQL not configured, returning empty history entries");
            return [];
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.DownloadHistory
                .OrderByDescending(e => e.FinishedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load history entries from PostgreSQL database, returning empty list");
            return [];
        }
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

        job.Status = JobStatus.Queued;
        job.Phase = JobPhase.Preparing;
        job.ProgressPercent = null;
        NotifyChanged();

        var slotAcquired = false;

        try
        {
            await _downloadSlots.WaitAsync(cts.Token);
            slotAcquired = true;

            int timeoutSeconds = int.TryParse(
                Environment.GetEnvironmentVariable("DOWNLOAD_TIMEOUT"), out int t) ? t : 3600;
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            NotifyChanged();

            int exitCode = await _downloader.DownloadAsync(
                job.Url,
                _downloadDir,
                _seriesDownloadDir,
                _documentaryDownloadDir,
                job.Title,
                job.Category,
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
                job.Logs += "\nDownload cancelled or timed out.";
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
            if (slotAcquired)
                _downloadSlots.Release();

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

    private static int ParseMaxConcurrentDownloads(IConfiguration configuration)
    {
        var raw = configuration["MAX_CONCURRENT_DOWNLOADS"]
                  ?? Environment.GetEnvironmentVariable("MAX_CONCURRENT_DOWNLOADS");

        if (!int.TryParse(raw, out var value) || value < 1)
            return 3;

        return Math.Min(value, 20);
    }

    private void CreateDirectoryWithUnixPermissions(string path)
    {
        Directory.CreateDirectory(path);
        SetUnixFilePermissions(path);
    }

    private void SetUnixFilePermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                // Set permissions to 0o777 (rwxrwxrwx) for user, group, and other
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not set Unix file mode for {Path}: {Message}", path, ex.Message);
            }
        }
    }
}
