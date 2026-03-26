using VoeDl.Web.Models;

namespace VoeDl.Web.Services;

/// <summary>
/// Abstraction over <see cref="JobManagerService"/> to enable dependency
/// injection of a test double during integration tests.
/// </summary>
public interface IJobManagerService
{
    /// <summary>Raised whenever any job changes state.</summary>
    event Action? JobsChanged;

    /// <summary>Returns all jobs ordered newest-first.</summary>
    IReadOnlyList<DownloadJob> GetJobs();

    /// <summary>Returns a single job or <see langword="null"/>.</summary>
    DownloadJob? GetJob(string id);

    /// <summary>Enqueues a new download job for <paramref name="url"/>.</summary>
    DownloadJob Enqueue(string url);

    /// <summary>Requests cancellation of a running or pending job.</summary>
    bool Abort(string id);

    /// <summary>Creates a new job for the same URL as an existing finished job.</summary>
    DownloadJob? Requeue(string id);

    /// <summary>Returns all URLs that were successfully downloaded.</summary>
    Task<IReadOnlyList<string>> LoadHistoryAsync();

    /// <summary>Returns full history entries ordered newest-first.</summary>
    Task<IReadOnlyList<DownloadHistoryEntry>> LoadHistoryEntriesAsync();
}
