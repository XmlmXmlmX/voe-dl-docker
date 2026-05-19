using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Runtime.InteropServices;
using AngleSharp;
using AngleSharp.Html.Dom;
using Microsoft.AspNetCore.WebUtilities;

namespace VoeDl.Web.Services;

/// <summary>
/// Extracts the playable media URL from a voe.sx page using the same
/// eight detection methods that were implemented in the original dl.py.
/// The actual file download is delegated to the yt-dlp binary.
/// </summary>
public sealed class DownloadService
{
    private sealed record EpisodeContext(string SeriesName, int Season, int Episode);

    // ---------------------------------------------------------------
    // Constants / configuration
    // ---------------------------------------------------------------
    private const int HttpTimeoutSeconds = 30;
    private const int MinDelayMs = 1_000;
    private const int MaxDelayMs = 3_000;
    private const int DownloadBufferSize = 81_920;        // 80 KB streaming buffer
    private const long DownloadLogIntervalBytes = 10 * 1_048_576; // log every 10 MB

    private static readonly string[] KnownVideoExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv", ".webm", ".ts", ".m4v"];

    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    ];

    private static readonly string[] BaitFilenames =
        ["BigBuckBunny", "Big_Buck_Bunny_1080_10s_5MB", "bbb.mp4"];

    private static readonly Regex VideoExtensionRegex =
        new(@"\.(mp4|mkv|avi|mov|flv|wmv|webm|ts|m4v)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] BaitDomains =
        ["test-videos.co.uk", "sample-videos.com", "commondatastorage.googleapis.com"];

    private static readonly string[] RedirectPatterns =
    [
        "window.location.href = '",
        "window.location = '",
        "location.href = '",
        "window.location.replace('",
        "window.location.assign('",
        "window.location.href = \"",
        "window.location = \"",
        "location.href = \"",
    ];

    private static readonly string[] ObfuscationPatterns = ["@$", "^^", "~@", "%?", "*~", "!!", "#&"];

    private static readonly string[] TruthyValues = ["1", "true", "yes", "on"];

    private static readonly Regex StoRootRegex = new(
        @"^https?://(?:www\.)?s\.to/serie/(?<slug>[a-z0-9\-]+?)/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex StoSeasonRegex = new(
        @"^https?://(?:www\.)?s\.to/serie/(?<slug>[a-z0-9\-]+?)/staffel-(?<season>\d+)/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex StoEpisodeRegex = new(
        @"^https?://(?:www\.)?s\.to/serie/(?<slug>[a-z0-9\-]+?)/staffel-(?<season>\d+)/episode-(?<episode>\d+)/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ---------------------------------------------------------------
    // Dependencies
    // ---------------------------------------------------------------
    private readonly HttpClient _http;
    private readonly HttpClient _downloadHttp;
    private readonly TmdbService _tmdb;
    private readonly MediathekViewWebService _mediathek;
    private readonly ILogger<DownloadService> _logger;
    private readonly Random _rng = new();

    public DownloadService(
        IHttpClientFactory httpClientFactory,
        TmdbService tmdbService,
        MediathekViewWebService mediathekViewWebService,
        ILogger<DownloadService> logger)
    {
        _http = httpClientFactory.CreateClient("voe");
        _downloadHttp = httpClientFactory.CreateClient("voe-download");
        _tmdb = tmdbService;
        _mediathek = mediathekViewWebService;
        _logger = logger;
    }

    // ---------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------

    public async Task<IReadOnlyList<string>> ExpandInputUrlAsync(
        string inputUrl,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUrl(inputUrl);

        if (MediathekViewWebService.TryParseSearchInput(normalized, out var mediathekQuery, out var mediathekEverywhere))
        {
            logCallback?.Invoke($"[*] Resolving MediathekViewWeb search: {mediathekQuery}");
            var searchResults = await _mediathek.SearchAsync(mediathekQuery, mediathekEverywhere, cancellationToken);
            return searchResults
                .Select(r => r.MediaUrl)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (StoEpisodeRegex.IsMatch(normalized))
            return [normalized];

        var seasonMatch = StoSeasonRegex.Match(normalized);
        if (seasonMatch.Success)
        {
            var seasonSlug = seasonMatch.Groups["slug"].Value;
            var seasonEpisodes = await CollectEpisodeUrlsAsync(normalized, seasonSlug, cancellationToken);
            var uniqueSeasonEpisodes = seasonEpisodes
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(NormalizeUrl)
                .Select(url => (Url: url, Ctx: TryParseStoEpisodeContext(url)))
                .Where(x => x.Ctx is not null)
                .OrderBy(x => x.Ctx!.Season)
                .ThenBy(x => x.Ctx!.Episode)
                .Select(x => x.Url)
                .ToList();

            return uniqueSeasonEpisodes.Count > 0 ? uniqueSeasonEpisodes : [normalized];
        }

        var rootMatch = StoRootRegex.Match(normalized);
        if (!rootMatch.Success)
            return [normalized];

        var slug = rootMatch.Groups["slug"].Value;
        logCallback?.Invoke($"[*] Resolving series URL: {normalized}");

        var seasonLinks = await CollectSeasonUrlsAsync(normalized, slug, cancellationToken);
        if (seasonLinks.Count == 0)
        {
            logCallback?.Invoke("[!] No seasons found on series page. Falling back to original URL.");
            return [normalized];
        }

        var episodeUrls = new List<string>();
        foreach (var seasonUrl in seasonLinks)
        {
            var episodesForSeason = await CollectEpisodeUrlsAsync(seasonUrl, slug, cancellationToken);
            episodeUrls.AddRange(episodesForSeason);
        }

        var uniqueEpisodes = episodeUrls
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(NormalizeUrl)
            .Select(url => (Url: url, Ctx: TryParseStoEpisodeContext(url)))
            .Where(x => x.Ctx is not null)
            .OrderBy(x => x.Ctx!.Season)
            .ThenBy(x => x.Ctx!.Episode)
            .Select(x => x.Url)
            .ToList();

        if (uniqueEpisodes.Count == 0)
        {
            logCallback?.Invoke("[!] No episodes found on season pages. Falling back to original URL.");
            return [normalized];
        }

        logCallback?.Invoke($"[+] Enqueueing {uniqueEpisodes.Count} episodes from series URL.");
        return uniqueEpisodes;
    }

    /// <summary>
    /// Downloads the video at <paramref name="url"/> into
    /// <paramref name="downloadDir"/>.
    /// Progress/log lines are written to <paramref name="logCallback"/>.
    /// Returns the exit/status code (0 = success).
    /// </summary>
    public async Task<int> DownloadAsync(
        string url,
        string downloadDir,
        string? seriesDownloadDir,
        string? documentaryDownloadDir,
        string? overrideTitle,
        Models.DownloadCategory category,
        Action<string> logCallback,
        CancellationToken cancellationToken = default)
    {
        // Random delay to mimic human behaviour
        await Task.Delay(_rng.Next(MinDelayMs, MaxDelayMs), cancellationToken);

        string sourceUrl;
        string mediaType;
        string name;
        string folderName;

        if (TryParseDirectMediaUrl(url, out mediaType, out var directName))
        {
            sourceUrl = url;
            name = string.IsNullOrWhiteSpace(directName)
                ? MakeFolderName(url)
                : MakeFolderName(directName);
            folderName = directName ?? string.Empty;
            logCallback($"[*] Direct media URL detected: {sourceUrl}");
        }
        else
        {
            var extracted = await ExtractSourceAsync(url, logCallback, cancellationToken);
            if (extracted.Url is null)
            {
                logCallback("[!] Could not find a downloadable URL.");
                return 1;
            }

            sourceUrl = extracted.Url;
            mediaType = extracted.MediaType;
            name = extracted.Name;
            folderName = extracted.FolderName;
        }

        if (!string.IsNullOrWhiteSpace(overrideTitle))
        {
            name = overrideTitle.Trim();
            folderName = name;
        }

        var episodeContext = TryParseStoEpisodeContext(url);
        var resolvedSeriesRoot = string.IsNullOrWhiteSpace(seriesDownloadDir)
            ? downloadDir
            : seriesDownloadDir;

        var createSubfolder = ShouldCreateSubfolder();

        string outputDir;
        var tmdbLookupTitle = name;
        var baseName = $"{SanitizeFilename(name)}_SS";

        // Check manual category override first
        bool forceSeries = category == Models.DownloadCategory.Series;
        bool forceMovie = category == Models.DownloadCategory.Movie;
        bool forceDocumentary = category == Models.DownloadCategory.Documentary;

        if (forceSeries || episodeContext is not null)
        {
            string seriesName;
            int? season = null;
            int? episode = null;

            if (episodeContext is not null)
            {
                seriesName = episodeContext.SeriesName;
                season = episodeContext.Season;
                episode = episodeContext.Episode;
            }
            else if (forceSeries)
            {
                seriesName = ExtractSeriesNameFromTitle(name);
                var seasonEpisode = TryExtractSeasonEpisodeFromTitle(name);
                if (seasonEpisode != null)
                {
                    season = seasonEpisode.Season;
                    episode = seasonEpisode.Episode;
                }
            }
            else
            {
                seriesName = name;
            }

            var seriesDirName = SanitizePath(seriesName);
            var seriesRootDir = Path.Combine(resolvedSeriesRoot, seriesDirName);
            var seasonDir = season.HasValue
                ? $"Season {season.Value:00}"
                : "Season 01";
            outputDir = Path.Combine(seriesRootDir, seasonDir);

            if (forceSeries)
                logCallback($"[*] Manual category override: forcing series directory: {outputDir}");
            else
                logCallback($"[*] URL identified as series episode, using series directory: {outputDir}");

            if (episodeContext is not null)
            {
                baseName = SanitizeFilename(
                    $"{episodeContext.SeriesName} S{episodeContext.Season:00}E{episodeContext.Episode:00}");
                name = episodeContext.SeriesName;
                tmdbLookupTitle = $"{episodeContext.SeriesName} S{episodeContext.Season:00}E{episodeContext.Episode:00}";
            }
            else if (forceSeries && season.HasValue && episode.HasValue)
            {
                baseName = SanitizeFilename(
                    $"{seriesName} S{season.Value:00}E{episode.Value:00}");
                name = seriesName;
                tmdbLookupTitle = $"{seriesName} S{season.Value:00}E{episode.Value:00}";
            }
        }
        else if (forceMovie)
        {
            outputDir = (createSubfolder && !string.IsNullOrWhiteSpace(folderName))
                ? Path.Combine(downloadDir, SanitizePath(folderName))
                : downloadDir;

            logCallback($"[*] Manual category override: forcing movie directory: {outputDir}");
        }
        else if (forceDocumentary)
        {
            string resolvedDocumentaryRoot = string.IsNullOrWhiteSpace(documentaryDownloadDir)
                ? downloadDir
                : documentaryDownloadDir;

            outputDir = (createSubfolder && !string.IsNullOrWhiteSpace(folderName))
                ? Path.Combine(resolvedDocumentaryRoot, SanitizePath(folderName))
                : resolvedDocumentaryRoot;

            logCallback($"[*] Manual category override: forcing documentary directory: {outputDir}");
        }
        else
        {
            // Auto detection logic
            if (episodeContext is not null)
            {
                var seriesDirName = SanitizePath(episodeContext.SeriesName);
                var seriesRootDir = Path.Combine(resolvedSeriesRoot, seriesDirName);
                var seasonDir = $"Season {episodeContext.Season:00}";
                outputDir = Path.Combine(seriesRootDir, seasonDir);
                logCallback($"[*] URL identified as series episode, using series directory: {outputDir}");

                baseName = SanitizeFilename(
                    $"{episodeContext.SeriesName} S{episodeContext.Season:00}E{episodeContext.Episode:00}");
                name = episodeContext.SeriesName;
                tmdbLookupTitle = $"{episodeContext.SeriesName} S{episodeContext.Season:00}E{episodeContext.Episode:00}";
            }
            else
            {
                outputDir = (createSubfolder && !string.IsNullOrWhiteSpace(folderName))
                    ? Path.Combine(downloadDir, SanitizePath(folderName))
                    : downloadDir;
            }
        }

        CreateDirectoryWithUnixPermissions(outputDir);

        // Only do TMDB lookup if category is Auto
        Models.TmdbMetadata? resolvedMetadata = null;
        if (category == Models.DownloadCategory.Auto)
        {
            resolvedMetadata = await _tmdb.LookupAsync(tmdbLookupTitle, cancellationToken);
            if (episodeContext is null &&
                resolvedMetadata?.Kind == Models.TmdbMediaKind.TvEpisode &&
                !string.IsNullOrWhiteSpace(seriesDownloadDir))
            {
                var seriesName = resolvedMetadata.ShowTitle ?? resolvedMetadata.Title;
                var seriesDirName = SanitizePath(seriesName);
                var seasonDir = $"Season {(resolvedMetadata.SeasonNumber ?? 1):00}";
                outputDir = Path.Combine(resolvedSeriesRoot, seriesDirName, seasonDir);

                if (resolvedMetadata.SeasonNumber.HasValue && resolvedMetadata.EpisodeNumber.HasValue)
                {
                    baseName = SanitizeFilename(
                        $"{seriesName} S{resolvedMetadata.SeasonNumber.Value:00}E{resolvedMetadata.EpisodeNumber.Value:00}");
                }

                logCallback($"[*] Routed download to series directory: {outputDir}");
            }
        }

        if (!Directory.Exists(outputDir))
            CreateDirectoryWithUnixPermissions(outputDir);

        if (resolvedMetadata is not null)
            logCallback($"[+] TMDB: matched \"{resolvedMetadata.Title}\" ({resolvedMetadata.Kind}, id={resolvedMetadata.TmdbId})");
        else
            logCallback("[*] TMDB: no match found or token not configured.");

        var existingEpisodeFile = TryFindExistingEpisodeFile(outputDir, resolvedMetadata);
        if (existingEpisodeFile is not null)
        {
            logCallback($"[*] Episode already exists in target folder, skipping download: {existingEpisodeFile}");
            await WriteResolvedNfoAsync(resolvedMetadata, existingEpisodeFile, logCallback, cancellationToken);
            return 0;
        }

        logCallback($"[*] Downloading {mediaType} stream: {sourceUrl}");

        int exitCode;
        string? outputPath;

        // Direct MP4 URLs are downloaded via HttpClient to avoid requiring yt-dlp locally.
        // HLS/DASH manifests still need yt-dlp for muxing.
        if (mediaType == "mp4")
        {
            outputPath = Path.Combine(outputDir, $"{baseName}.mp4");
            logCallback($"[*] Output path: {outputPath}");
            exitCode = await DownloadMp4DirectAsync(sourceUrl, outputPath, logCallback, cancellationToken);
        }
        else
        {
            var outputTemplate = Path.Combine(outputDir, $"{baseName}.%(ext)s");
            logCallback($"[*] Output path: {outputTemplate}");
            exitCode = await RunYtDlpAsync(sourceUrl, outputTemplate, logCallback, cancellationToken);
            outputPath = exitCode == 0 ? FindCreatedFile(outputDir, baseName) : null;
        }

        if (exitCode == 0 && outputPath is not null)
        {
            if (resolvedMetadata is not null)
                await WriteResolvedNfoAsync(resolvedMetadata, outputPath, logCallback, cancellationToken);
            else
                await LookupAndWriteNfoAsync(tmdbLookupTitle, outputPath, logCallback, cancellationToken);
        }

        return exitCode;
    }

    // ---------------------------------------------------------------
    // Source extraction
    // ---------------------------------------------------------------

    private async Task<List<string>> CollectSeasonUrlsAsync(string seriesUrl, string slug, CancellationToken ct)
    {
        var html = await FetchHtmlAsync(seriesUrl, ct);
        if (html is null) return [];

        var seasonUrls = ExtractLinks(html, seriesUrl)
            .Select(NormalizeUrl)
            .Where(link => MatchesSlug(StoSeasonRegex.Match(link), slug))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(link => ParseSeasonNumberFromSeasonUrl(link))
            .ToList();

        return seasonUrls;
    }

    private async Task<List<string>> CollectEpisodeUrlsAsync(string seasonUrl, string slug, CancellationToken ct)
    {
        var html = await FetchHtmlAsync(seasonUrl, ct);
        if (html is null) return [];

        var episodeUrls = ExtractLinks(html, seasonUrl)
            .Select(NormalizeUrl)
            .Where(link => MatchesSlug(StoEpisodeRegex.Match(link), slug))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return episodeUrls;
    }

    private async Task<string?> FetchHtmlAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddBrowserHeaders(request, url);
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch page while expanding URL: {Url}", url);
            return null;
        }
    }

    private static IEnumerable<string> ExtractLinks(string html, string baseUrl)
    {
        var regex = new Regex("href\\s*=\\s*[\"'](?<href>[^\"'#>\\s]+)[\"']", RegexOptions.IgnoreCase);
        var baseUri = new Uri(baseUrl);

        foreach (Match m in regex.Matches(html))
        {
            var href = m.Groups["href"].Value.Trim();
            if (string.IsNullOrWhiteSpace(href))
                continue;

            if (href.StartsWith("//", StringComparison.Ordinal))
                href = "https:" + href;

            if (!Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                && Uri.TryCreate(baseUri, href, out var combined))
            {
                absolute = combined;
            }

            if (absolute is not null)
                yield return absolute.ToString();
        }
    }

    private static bool MatchesSlug(Match match, string expectedSlug)
    {
        if (!match.Success) return false;
        var slug = match.Groups["slug"].Value;
        return slug.Equals(expectedSlug, StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseSeasonNumberFromSeasonUrl(string seasonUrl)
    {
        var match = StoSeasonRegex.Match(seasonUrl);
        return match.Success && int.TryParse(match.Groups["season"].Value, out var season)
            ? season
            : int.MaxValue;
    }

    private static EpisodeContext? TryParseStoEpisodeContext(string url)
    {
        var match = StoEpisodeRegex.Match(NormalizeUrl(url));
        if (!match.Success) return null;

        if (!int.TryParse(match.Groups["season"].Value, out var season)) return null;
        if (!int.TryParse(match.Groups["episode"].Value, out var episode)) return null;

        var slug = match.Groups["slug"].Value;
        var seriesName = SlugToTitle(slug);
        return new EpisodeContext(seriesName, season, episode);
    }

    private static void CreateDirectoryWithUnixPermissions(string path)
    {
        Directory.CreateDirectory(path);
        SetUnixFilePermissions(path);
    }

    private static void SetUnixFilePermissions(string path)
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
                // Log warning but don't fail - permissions might be set by container
                System.Diagnostics.Debug.WriteLine($"Warning: Could not set Unix file mode for {path}: {ex.Message}");
            }
        }
    }

    private sealed class SeasonEpisode
    {
        public int Season { get; }
        public int Episode { get; }

        public SeasonEpisode(int season, int episode)
        {
            Season = season;
            Episode = episode;
        }
    }

    private static string ExtractSeriesNameFromTitle(string title)
    {
        // Check if title is already in "SeriesName - EpisodeTitle" format from Mediathek
        var dashIndex = title.IndexOf(" - ");
        if (dashIndex > 0)
        {
            return title[..dashIndex].Trim();
        }

        // Fallback: Remove everything after the first opening parenthesis or bracket
        var index = title.IndexOfAny(['(', '[']);
        if (index > 0)
            return title[..index].Trim();
        return title.Trim();
    }

    private static SeasonEpisode? TryExtractSeasonEpisodeFromTitle(string title)
    {
        // Look for patterns like (S04_E07), [S04E07], S04E07, etc.
        var patterns = new[]
        {
            @"\(S(?<season>\d+)_E(?<episode>\d+)\)",                // (S04_E07) - spezifisch für Klammern
            @"[(\[]S(?<season>\d+)[_\-\.]*E(?<episode>\d+)[)\]]",   // (S04_E07), [S04E07], (S04E07) - mehr Trennzeichen
            @"S(?<season>\d+)[_\-\.]*E(?<episode>\d+)",              // S04_E07, S04E07
            @"Staffel\s*(?<season>\d+).*?Episode\s*(?<episode>\d+)", // Staffel 4 Episode 7
            @"Season\s*(?<season>\d+).*?Episode\s*(?<episode>\d+)"   // Season 4 Episode 7
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(title, pattern, RegexOptions.IgnoreCase);
            if (match.Success &&
                int.TryParse(match.Groups["season"].Value, out var season) &&
                int.TryParse(match.Groups["episode"].Value, out var episode))
            {
                return new SeasonEpisode(season, episode);
            }
        }

        return null;
    }

    private static string SlugToTitle(string slug)
    {
        var words = slug
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(" ", words);
    }

    private static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.EndsWith('/'))
            trimmed = trimmed.TrimEnd('/');
        return trimmed;
    }

    private static bool TryParseDirectMediaUrl(string url, out string mediaType, out string fileName)
    {
        mediaType = string.Empty;
        fileName = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        if (extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            mediaType = "hls";
            fileName = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            return true;
        }

        if (KnownVideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            mediaType = extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                ? "mp4"
                : extension.TrimStart('.').ToLowerInvariant();
            fileName = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            return true;
        }

        return false;
    }

    private async Task<(string? Url, string MediaType, string Name, string FolderName)> ExtractSourceAsync(
        string url,
        Action<string> log,
        CancellationToken ct,
        int depth = 0)
    {
        if (depth > 5)
        {
            log("[!] Redirect depth limit reached.");
            return (null, "", "", "");
        }

        string pageHtml;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddBrowserHeaders(request, url);
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            pageHtml = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            log($"[!] HTTP error fetching {url}: {ex.Message}");
            return (null, "", "", "");
        }

        // Parse HTML with AngleSharp
        var config = Configuration.Default;
        using var context = BrowsingContext.New(config);
        using var document = await context.OpenAsync(req => req.Content(pageHtml), ct) as IHtmlDocument
                             ?? throw new InvalidOperationException("Failed to parse HTML.");

        // ---- Check for client-side redirects ----
        foreach (var script in document.Scripts)
        {
            if (string.IsNullOrWhiteSpace(script.InnerHtml)) continue;
            foreach (var pattern in RedirectPatterns)
            {
                int idx = script.InnerHtml.IndexOf(pattern, StringComparison.Ordinal);
                if (idx < 0) continue;
                char closingChar = pattern.EndsWith("'") ? '\'' : '"';
                int start = idx + pattern.Length;
                int end = script.InnerHtml.IndexOf(closingChar, start);
                if (end <= start) continue;
                var redirectUrl = script.InnerHtml[start..end];
                log($"[*] Detected redirect to: {redirectUrl}");
                return await ExtractSourceAsync(redirectUrl, log, ct, depth + 1);
            }
        }

        // ---- Extract page title / file name ----
        string name = ExtractTitle(document, url);
        string folderName = MakeFolderName(name);
        name = SanitizeFilename(name);
        log($"Name of file: {name}");

        // ---- Method 1: var sources pattern ----
        var (srcUrl, mediaType) = TryMethod1VarSources(pageHtml, log);

        // ---- Method 2: script tags with sources ----
        if (srcUrl is null)
            (srcUrl, mediaType) = TryMethod2ScriptSources(document, log);

        // ---- Method 3: <video> / <source> tags ----
        if (srcUrl is null)
            (srcUrl, mediaType) = TryMethod3VideoTags(document, log);

        // ---- Method 4: regex for direct m3u8/mp4 URLs ----
        if (srcUrl is null)
            (srcUrl, mediaType) = TryMethod4DirectUrls(pageHtml, log);

        // ---- Method 5: base64-encoded URLs ----
        if (srcUrl is null)
            (srcUrl, mediaType) = TryMethod5Base64(pageHtml, log);

        // ---- Method 6: a168c encoded sources ----
        if (srcUrl is null)
            (srcUrl, mediaType) = TryMethod6A168c(pageHtml, log);

        // ---- Method 7: MKGMa encoded sources ----
        if (srcUrl is null)
            (srcUrl, mediaType) = TryMethod7MKGMa(pageHtml, log);

        // ---- Method 8: obfuscated JSON in <script type="application/json"> ----
        if (srcUrl is null)
            (srcUrl, mediaType) = TryMethod8ObfuscatedJson(document, log);

        // ---- iframe fallback ----
        if (srcUrl is null)
        {
            var iframe = document.QuerySelectorAll("iframe").FirstOrDefault();
            if (iframe is not null)
            {
                var iframeSrc = iframe.GetAttribute("src") ?? string.Empty;
                if (iframeSrc.StartsWith("//")) iframeSrc = "https:" + iframeSrc;
                if (!iframeSrc.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var baseUri = new Uri(url);
                    iframeSrc = new Uri(baseUri, iframeSrc).ToString();
                }

                if (TryResolveAuthRedirectTarget(iframeSrc, out var resolvedTarget))
                {
                    log($"[*] Resolved auth redirect iframe target: {resolvedTarget}");
                    iframeSrc = resolvedTarget;
                }

                var baseUri2 = new Uri(url);
                var iframeUri = new Uri(iframeSrc);
                if (!iframeUri.Host.Equals(baseUri2.Host, StringComparison.OrdinalIgnoreCase))
                {
                    log($"[*] Found external iframe source, treating as media URL: {iframeSrc}");
                    return (iframeSrc, "", name, folderName);
                }

                log($"[*] Found iframe, following to: {iframeSrc}");
                var iframeResult = await ExtractSourceAsync(iframeSrc, log, ct, depth + 1);
                return (iframeResult.Url, iframeResult.MediaType, name, folderName);
            }

            log("[!] Could not find sources in the page. The site structure might have changed.");
            SaveDebugPage(pageHtml, log);
            return (null, "", name, folderName);
        }

        return (srcUrl, mediaType, name, folderName);
    }

    // ---------------------------------------------------------------
    // Method 1 – var sources pattern
    // ---------------------------------------------------------------
    private (string? Url, string MediaType) TryMethod1VarSources(string html, Action<string> log)
    {
        int idx = html.IndexOf("var sources", StringComparison.Ordinal);
        if (idx < 0) return (null, "");
        try
        {
            int end = html.IndexOf(';', idx);
            if (end < 0) return (null, "");
            string raw = html[idx..end]
                .Replace("var sources = ", "")
                .Replace("'", "\"")
                .Replace("\\n", "")
                .Replace("\\", "");

            if (IsBaitSource(raw)) { log($"[!] Ignoring bait source: {raw}"); return (null, ""); }

            // Remove trailing comma before last }
            int lastComma = raw.LastIndexOf(',');
            if (lastComma >= 0) raw = string.Concat(raw.AsSpan(0, lastComma), raw.AsSpan(lastComma + 1));

            using var doc = JsonDocument.Parse(raw);
            return ExtractFromJsonElement(doc.RootElement, log, "[+] Found sources using var sources pattern");
        }
        catch (Exception ex)
        {
            log($"[!] Method 1 error: {ex.Message}");
            return (null, "");
        }
    }

    // ---------------------------------------------------------------
    // Method 2 – script tags with sources
    // ---------------------------------------------------------------
    private static readonly string[] SourcePatterns = ["var sources", "sources =", "sources:", "\"sources\":", "'sources':"];

    private (string? Url, string MediaType) TryMethod2ScriptSources(IHtmlDocument document, Action<string> log)
    {
        foreach (var script in document.Scripts)
        {
            if (string.IsNullOrWhiteSpace(script.InnerHtml)) continue;
            string text = script.InnerHtml;
            foreach (var pattern in SourcePatterns)
            {
                int idx = text.IndexOf(pattern, StringComparison.Ordinal);
                if (idx < 0) continue;
                int braceStart = text.IndexOf('{', idx);
                if (braceStart < 0) continue;
                int depth = 1, pos = braceStart + 1;
                while (depth > 0 && pos < text.Length)
                {
                    if (text[pos] == '{') depth++;
                    else if (text[pos] == '}') depth--;
                    pos++;
                }
                if (depth != 0) continue;
                string jsonStr = text[braceStart..pos].Replace("'", "\"");
                try
                {
                    using var doc = JsonDocument.Parse(jsonStr);
                    var (url, mediaType) = ExtractFromJsonElement(doc.RootElement, log,
                        $"[+] Found sources using pattern: {pattern}");
                    if (url is not null) return (url, mediaType);
                }
                catch { /* try next */ }
            }
        }
        return (null, "");
    }

    // ---------------------------------------------------------------
    // Method 3 – <video>/<source> tags
    // ---------------------------------------------------------------
    private (string? Url, string MediaType) TryMethod3VideoTags(IHtmlDocument document, Action<string> log)
    {
        foreach (var video in document.QuerySelectorAll("video"))
        {
            var src = video.GetAttribute("src");
            if (!string.IsNullOrWhiteSpace(src))
            {
                if (IsBaitSource(src)) { log($"[!] Ignoring bait source: {src}"); continue; }
                log($"[+] Found direct video source: {src}");
                return (src, "mp4");
            }
            foreach (var sourceTag in video.QuerySelectorAll("source"))
            {
                var tagSrc = sourceTag.GetAttribute("src");
                if (string.IsNullOrWhiteSpace(tagSrc)) continue;
                if (IsBaitSource(tagSrc)) { log($"[!] Ignoring bait source: {tagSrc}"); continue; }
                var typeAttr = sourceTag.GetAttribute("type") ?? "";
                string mediaType = typeAttr.Contains("m3u8") || typeAttr.Contains("hls") ? "hls" : "mp4";
                log($"[+] Found video source from source tag: {tagSrc}");
                return (tagSrc, mediaType);
            }
        }
        return (null, "");
    }

    // ---------------------------------------------------------------
    // Method 4 – direct m3u8/mp4 URLs in page text
    // ---------------------------------------------------------------
    private (string? Url, string MediaType) TryMethod4DirectUrls(string html, Action<string> log)
    {
        log("[*] Searching for direct media URLs in page...");
        var m3u8 = Regex.Match(html, @"(https?://[^""'\s]+\.m3u8[^""'\s]*)");
        if (m3u8.Success && !IsBaitSource(m3u8.Value))
        {
            log($"[+] Found HLS URL: {m3u8.Value}");
            return (m3u8.Value, "hls");
        }
        var mp4 = Regex.Match(html, @"(https?://[^""'\s]+\.mp4[^""'\s]*)");
        if (mp4.Success && !IsBaitSource(mp4.Value))
        {
            log($"[+] Found MP4 URL: {mp4.Value}");
            return (mp4.Value, "mp4");
        }
        return (null, "");
    }

    // ---------------------------------------------------------------
    // Method 5 – base64-encoded URLs
    // ---------------------------------------------------------------
    private (string? Url, string MediaType) TryMethod5Base64(string html, Action<string> log)
    {
        foreach (Match m in Regex.Matches(html, @"base64[,:]([A-Za-z0-9+/=]+)"))
        {
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
                if (decoded.Contains(".mp4")) { log("[+] Found base64 encoded MP4 URL"); return (decoded, "mp4"); }
                if (decoded.Contains(".m3u8")) { log("[+] Found base64 encoded HLS URL"); return (decoded, "hls"); }
            }
            catch { /* ignore bad base64 */ }
        }
        return (null, "");
    }

    // ---------------------------------------------------------------
    // Method 6 – a168c encoded sources
    // ---------------------------------------------------------------
    private (string? Url, string MediaType) TryMethod6A168c(string html, Action<string> log)
    {
        log("[*] Searching for a168c encoded sources...");
        var match = Regex.Match(html, @"a168c\s*=\s*'([^']+)'", RegexOptions.Singleline);
        if (!match.Success) return (null, "");
        try
        {
            string cleaned = CleanBase64(match.Groups[1].Value);
            byte[] decoded = Convert.FromBase64String(cleaned);
            string text = new string(Encoding.UTF8.GetString(decoded).Reverse().ToArray());
            return ParseDecodedMediaJson(text, log);
        }
        catch (Exception ex)
        {
            log($"[!] Failed to decode a168c string: {ex.Message}");
            return (null, "");
        }
    }

    // ---------------------------------------------------------------
    // Method 7 – MKGMa encoded sources
    // ---------------------------------------------------------------
    private (string? Url, string MediaType) TryMethod7MKGMa(string html, Action<string> log)
    {
        log("[*] Searching for MKGMa sources...");
        var match = Regex.Match(html, @"MKGMa=""(.*?)""", RegexOptions.Singleline);
        if (!match.Success) return (null, "");
        try
        {
            string step1 = Rot13(match.Groups[1].Value);
            string step2 = step1.Replace("_", "");
            string step3 = Encoding.UTF8.GetString(Convert.FromBase64String(step2));
            string step4 = ShiftChars(step3, 3);
            string step5 = new string(step4.Reverse().ToArray());
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(step5));
            return ParseDecodedMediaJson(decoded, log);
        }
        catch (Exception ex)
        {
            log($"[-] Error while decoding MKGMa string: {ex.Message}");
            return (null, "");
        }
    }

    // ---------------------------------------------------------------
    // Method 8 – obfuscated JSON in <script type="application/json">
    // ---------------------------------------------------------------
    private (string? Url, string MediaType) TryMethod8ObfuscatedJson(IHtmlDocument document, Action<string> log)
    {
        log("[*] Searching for obfuscated JSON sources...");
        foreach (var script in document.QuerySelectorAll("script[type='application/json']"))
        {
            string candidate = script.InnerHtml.Trim();
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var result = DeobfuscateEmbeddedJson(candidate);
            if (result is null) continue;

            if (result is JsonElement elem)
            {
                var (url, mediaType) = ExtractFromJsonElement(elem, log,
                    "[+] Found media URL in obfuscated JSON");
                if (url is not null) return (url, mediaType);
            }
            else if (result is string str)
            {
                var mp4 = Regex.Match(str, @"(https?://[^\s""]+\.mp4[^\s""]*)");
                if (mp4.Success) { log("[+] Extracted mp4 from obfuscated JSON string"); return (mp4.Value, "mp4"); }
                var m3u8 = Regex.Match(str, @"(https?://[^\s""]+\.m3u8[^\s""]*)");
                if (m3u8.Success) { log("[+] Extracted m3u8 from obfuscated JSON string"); return (m3u8.Value, "hls"); }
            }
        }
        return (null, "");
    }

    // ---------------------------------------------------------------
    // TMDB metadata + NFO
    // ---------------------------------------------------------------

    private async Task WriteResolvedNfoAsync(
        Models.TmdbMetadata? meta,
        string outputPath,
        Action<string> log,
        CancellationToken ct)
    {
        if (meta is null)
            return;

        await _tmdb.WriteNfoAsync(outputPath, meta);
        log($"[+] TMDB: NFO written → {Path.ChangeExtension(outputPath, ".nfo")}");

        if (meta.Kind == Models.TmdbMediaKind.TvEpisode && ShouldWriteTvShowNfo())
        {
            var seasonDir = Path.GetDirectoryName(outputPath);
            var showDir = !string.IsNullOrWhiteSpace(seasonDir)
                ? Directory.GetParent(seasonDir)?.FullName ?? seasonDir
                : Path.GetDirectoryName(outputPath) ?? ".";

            if (await _tmdb.WriteTvShowNfoAsync(showDir, meta, ct))
                log($"[+] TMDB: tvshow.nfo written → {Path.Combine(showDir, "tvshow.nfo")}");
        }
    }

    private static string? TryFindExistingEpisodeFile(string outputDir, Models.TmdbMetadata? meta)
    {
        if (meta?.Kind != Models.TmdbMediaKind.TvEpisode
            || !meta.SeasonNumber.HasValue
            || !meta.EpisodeNumber.HasValue
            || !Directory.Exists(outputDir))
            return null;

        var marker = $"S{meta.SeasonNumber.Value:00}E{meta.EpisodeNumber.Value:00}";

        return Directory.EnumerateFiles(outputDir)
            .FirstOrDefault(path =>
            {
                var ext = Path.GetExtension(path);
                if (string.IsNullOrWhiteSpace(ext)
                    || !KnownVideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    return false;

                var fileName = Path.GetFileNameWithoutExtension(path);
                return fileName.Contains(marker, StringComparison.OrdinalIgnoreCase);
            });
    }

    private async Task LookupAndWriteNfoAsync(
        string rawTitle,
        string outputPath,
        Action<string> log,
        CancellationToken ct)
    {
        try
        {
            var meta = await _tmdb.LookupAsync(rawTitle, ct);
            if (meta is null)
            {
                log("[*] TMDB: no match found or token not configured — skipping NFO.");
                return;
            }
            log($"[+] TMDB: matched \"{meta.Title}\" ({meta.Kind}, id={meta.TmdbId})");
            await WriteResolvedNfoAsync(meta, outputPath, log, ct);
        }
        catch (Exception ex)
        {
            log($"[!] TMDB metadata lookup failed: {ex.Message}");
            _logger.LogWarning(ex, "TMDB metadata lookup failed for {OutputPath}", outputPath);
        }
    }

    /// <summary>
    /// After yt-dlp writes the file it replaces <c>%(ext)s</c> with the
    /// real extension.  Scan the directory for the created file.
    /// </summary>
    private static string? FindCreatedFile(string dir, string baseName)
    {
        try
        {
            return Directory
                .EnumerateFiles(dir, $"{baseName}.*")
                .FirstOrDefault(f =>
                    !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                    !f.EndsWith(".nfo",  StringComparison.OrdinalIgnoreCase) &&
                    !f.EndsWith(".tmp",  StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------
    // Direct MP4 downloader (no yt-dlp required)
    // ---------------------------------------------------------------
    private async Task<int> DownloadMp4DirectAsync(
        string mediaUrl,
        string outputPath,
        Action<string> log,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
            AddBrowserHeaders(request, mediaUrl);

            using var response = await _downloadHttp.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            string totalStr = totalBytes.HasValue
                ? $"{totalBytes.Value / 1_048_576.0:F1} MB"
                : "unknown size";
            log($"[*] File size: {totalStr}");

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: DownloadBufferSize, useAsync: true);

            var buffer = new byte[DownloadBufferSize];
            long written = 0;
            long nextLogAt = DownloadLogIntervalBytes;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                if (written >= nextLogAt)
                {
                    log($"[*] Downloaded {written / 1_048_576.0:F1} MB" +
                        (totalBytes.HasValue ? $" / {totalStr}" : ""));
                    nextLogAt += DownloadLogIntervalBytes;
                }
            }

            log($"[+] Done — {written / 1_048_576.0:F1} MB saved to {outputPath}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            log("[!] Download cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            log($"[!] Direct download failed: {ex.Message}");
            return 1;
        }
    }

    // ---------------------------------------------------------------
    // yt-dlp runner
    // ---------------------------------------------------------------
    private async Task<int> RunYtDlpAsync(
        string mediaUrl,
        string outputTemplate,
        Action<string> log,
        CancellationToken ct)
    {
        string ytDlp = FindYtDlp();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ytDlp,
            ArgumentList =
            {
                mediaUrl,
                "-o", outputTemplate,
                "--no-warnings",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static string FindYtDlp()
    {
        // Allow an explicit override via environment variable (useful on Windows dev machines)
        var envPath = Environment.GetEnvironmentVariable("YT_DLP_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            if (File.Exists(envPath))
                return envPath;
            throw new FileNotFoundException(
                $"YT_DLP_PATH is set to '{envPath}' but the file does not exist.");
        }

        // Build the list of candidates to probe.  On Windows we also look for
        // the .exe variant because IsOnPath() must match the actual filename.
        bool isWindows = OperatingSystem.IsWindows();
        var candidates = isWindows
            ? new[] { "yt-dlp.exe", "yt-dlp" }
            : new[] { "yt-dlp", "/usr/local/bin/yt-dlp", "/usr/bin/yt-dlp" };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) || IsOnPath(candidate))
                return candidate;
        }

        // Binary not found – give the user an actionable message instead of a
        // cryptic OS "file not found" error from Process.Start().
        throw new FileNotFoundException(
            "yt-dlp binary could not be found. " +
            "Install it (https://github.com/yt-dlp/yt-dlp#installation) and ensure it is on " +
            "your PATH, or set the YT_DLP_PATH environment variable to its full path.");
    }

    private static bool IsOnPath(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);

        // On Windows, also honour PATHEXT so we find both "yt-dlp" and "yt-dlp.exe".
        IEnumerable<string> candidates;
        if (OperatingSystem.IsWindows() && !Path.HasExtension(name))
        {
            var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            candidates = extensions.Select(ext => name + ext).Prepend(name);
        }
        else
        {
            candidates = new[] { name };
        }

        return paths.Any(p => candidates.Any(c => File.Exists(Path.Combine(p, c))));
    }

    private void AddBrowserHeaders(HttpRequestMessage request, string url)
    {
        string ua = UserAgents[_rng.Next(UserAgents.Length)];
        request.Headers.TryAddWithoutValidation("User-Agent", ua);
        request.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("DNT", "1");
        try
        {
            var uri = new Uri(url);
            request.Headers.TryAddWithoutValidation("Referer", $"{uri.Scheme}://{uri.Host}/");
        }
        catch { /* ignore */ }
    }

    private static bool TryResolveAuthRedirectTarget(string iframeSrc, out string targetUrl)
    {
        targetUrl = string.Empty;
        if (!Uri.TryCreate(iframeSrc, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue("continue", out var continueValues))
            return false;

        var continueUrl = continueValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(continueUrl) || !Uri.IsWellFormedUriString(continueUrl, UriKind.Absolute))
            return false;

        targetUrl = continueUrl;
        return true;
    }

    private static string ExtractTitle(IHtmlDocument document, string url)
    {
        string[] metaProps = ["og:title", "twitter:title", "title"];
        foreach (var prop in metaProps)
        {
            var meta = document.QuerySelector($"meta[property='{prop}']")
                       ?? document.QuerySelector($"meta[name='{prop}']");
            var content = meta?.GetAttribute("content");
            if (!string.IsNullOrWhiteSpace(content)) return content;
        }
        if (!string.IsNullOrWhiteSpace(document.Title)) return document.Title;
        string last = new Uri(url).Segments.LastOrDefault() ?? "";
        return string.IsNullOrWhiteSpace(last) ? $"download_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}" : last;
    }

    private static string MakeFolderName(string raw)
    {
        string s = Regex.Replace(raw, @"[\\/*?:""<>|]", "");
        s = Regex.Replace(s, @"\.(?=[a-zA-Z0-9])", " ");
        s = Regex.Replace(s, @"(?<=[a-zA-Z0-9])-(?=[a-zA-Z0-9])", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(s) ? "download" : s;
    }

    private static string SanitizeFilename(string name)
    {
        // Strip common video file extensions that may already be present in page titles.
        name = VideoExtensionRegex.Replace(name, "");
        name = Regex.Replace(name, @"[\\/*?:""<>|]", "_");
        return name.Replace(" ", "_");
    }

    private static string SanitizePath(string path) =>
        string.Join("_", path.Split(Path.GetInvalidPathChars())).Replace("/", "_");

    private static bool ShouldCreateSubfolder()
    {
        return IsTruthyEnvironmentVariable("CREATE_SUBFOLDER");
    }

    private static bool ShouldWriteTvShowNfo() =>
        IsTruthyEnvironmentVariable("WRITE_TVSHOW_NFO");

    private static bool IsTruthyEnvironmentVariable(string variableName)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var normalized = raw.Trim();
        return TruthyValues.Any(v => normalized.Equals(v, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBaitSource(string source)
    {
        if (BaitFilenames.Any(fn => source.Contains(fn, StringComparison.OrdinalIgnoreCase)))
            return true;
        try
        {
            var host = new Uri(source).Host;
            return BaitDomains.Any(d => host.Contains(d, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static (string? Url, string MediaType) ExtractFromJsonElement(JsonElement elem, Action<string> log, string successMessage)
    {
        if (elem.ValueKind == JsonValueKind.Object)
        {
            if (elem.TryGetProperty("direct_access_url", out var mp4Prop))
            {
                var v = mp4Prop.GetString();
                if (!string.IsNullOrWhiteSpace(v)) { log(successMessage); return (EnsureHttps(v), "mp4"); }
            }
            if (elem.TryGetProperty("mp4", out var mp4p))
            {
                var v = DecodeIfBase64(mp4p.GetString());
                if (!string.IsNullOrWhiteSpace(v)) { log(successMessage); return (EnsureHttps(v!), "mp4"); }
            }
            if (elem.TryGetProperty("source", out var hlsProp))
            {
                var v = hlsProp.GetString();
                if (!string.IsNullOrWhiteSpace(v)) { log(successMessage); return (EnsureHttps(v), "hls"); }
            }
            if (elem.TryGetProperty("hls", out var hlsp))
            {
                var v = hlsp.GetString();
                if (!string.IsNullOrWhiteSpace(v)) { log(successMessage); return (EnsureHttps(v!), "hls"); }
            }
        }
        return (null, "");
    }

    private static (string? Url, string MediaType) ParseDecodedMediaJson(string decoded, Action<string> log)
    {
        try
        {
            using var doc = JsonDocument.Parse(decoded);
            var (url, mediaType) = ExtractFromJsonElement(doc.RootElement, log, "[+] Found URL in decoded JSON.");
            if (url is not null) return (url, mediaType);
        }
        catch
        {
            // Not JSON – fall through to regex search
            log("[-] Decoded string is not valid JSON. Trying fallback regex search...");
        }
        var mp4 = Regex.Match(decoded, @"(https?://[^\s""]+\.mp4[^\s""]*)");
        if (mp4.Success) { log("[+] Found encoded MP4 URL."); return (mp4.Value, "mp4"); }
        var m3u8 = Regex.Match(decoded, @"(https?://[^\s""]+\.m3u8[^\s""]*)");
        if (m3u8.Success) { log("[+] Found encoded HLS URL."); return (m3u8.Value, "hls"); }
        return (null, "");
    }

    // ---- Crypto helpers ----

    private static string Rot13(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c >= 'A' && c <= 'Z') sb.Append((char)(((c - 'A' + 13) % 26) + 'A'));
            else if (c >= 'a' && c <= 'z') sb.Append((char)(((c - 'a' + 13) % 26) + 'a'));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ReplaceObfuscationPatterns(string text)
    {
        foreach (var pat in ObfuscationPatterns) text = text.Replace(pat, "");
        return text;
    }

    private static string ShiftChars(string text, int shift) =>
        new string(text.Select(c => (char)(c - shift)).ToArray());

    private static string CleanBase64(string s)
    {
        s = s.Replace("\\", "");
        int pad = s.Length % 4;
        if (pad != 0) s += new string('=', 4 - pad);
        return s;
    }

    private static string? DecodeIfBase64(string? value)
    {
        if (value is null) return null;
        if (!value.StartsWith("eyJ") && !Regex.IsMatch(value, @"^[A-Za-z0-9+/=]+$"))
            return value;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch { return value; }
    }

    private static string EnsureHttps(string url) =>
        url.StartsWith("//") ? "https:" + url : url;

    private static string? SafeB64Decode(string s)
    {
        int pad = s.Length % 4;
        if (pad != 0) s += new string('=', 4 - pad);
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return null; }
    }

    // ---- Method 8 deobfuscator ----
    private static object? DeobfuscateEmbeddedJson(string rawJson)
    {
        try
        {
            using var arr = JsonDocument.Parse(rawJson);
            if (arr.RootElement.ValueKind != JsonValueKind.Array
                || arr.RootElement.GetArrayLength() == 0) return null;
            string? obf = arr.RootElement[0].GetString();
            if (obf is null) return null;

            string step1 = Rot13(obf);
            string step2 = ReplaceObfuscationPatterns(step1);
            string? step3 = SafeB64Decode(step2);
            if (step3 is null) return null;
            string step4 = ShiftChars(step3, 3);
            string step5 = new string(step4.Reverse().ToArray());
            string? step6 = SafeB64Decode(step5);
            if (step6 is null) return null;

            try
            {
                var doc = JsonDocument.Parse(step6);
                return doc.RootElement.Clone();
            }
            catch
            {
                return step6;
            }
        }
        catch { return null; }
    }

    private void SaveDebugPage(string html, Action<string> log)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), $"debug_page_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.html");
            File.WriteAllText(path, html, Encoding.UTF8);
            SetUnixFilePermissions(path);
            log($"[*] Page content saved for debugging: {path}");
        }
        catch (Exception ex)
        {
            log($"[!] Could not save debug page: {ex.Message}");
        }
    }
}
