using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VoeDl.Web.Models;

namespace VoeDl.Web.Services;

/// <summary>
/// Resolves movie/TV episode metadata from The Movie Database (TMDB) API
/// and writes Jellyfin/Kodi-compatible .nfo sidecar files alongside the
/// downloaded video.
/// </summary>
public sealed class TmdbService
{
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/original";
    private const string ApiBase = "https://api.themoviedb.org/3/";

    // Matches common scene-release quality/codec/language tags that mark
    // the boundary between the clean title and the release metadata.
    private static readonly Regex ReleaseTagRe = new(
        @"(?<![A-Za-z])" +
        @"(720p|1080p|2160p|4[Kk]|UHD|SD|" +
        @"WEB[-.]?DL|WEB|BluRay|Blu[-.]Ray|BDRip|BRRip|DVDRip|HDTV|PDTV|AMZN|NF|DSNP|HMAX|ATVP|" +
        @"x264|x265|h264|h265|HEVC|AVC|AV1|XviD|" +
        @"AAC|AC3|DTS|TrueHD|FLAC|Atmos|EAC3|" +
        @"HDR10?|SDR|DoVi|HLG|" +
        @"GERMAN|ENGLISH|FRENCH|SPANISH|ITALIAN|DUTCH|SWEDISH|NORWEGIAN|DANISH|PORTUGUESE|POLISH|CZECH|RUSSIAN|MULTI|" +
        @"DUBBED|SUBBED|Forced|Subs?|" +
        @"EXTENDED|THEATRICAL|REMASTERED|PROPER|REPACK|INTERNAL|LIMITED|COMPLETE)" +
        @"(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EpisodeRe = new(
        @"[Ss](?<season>\d{1,2})[Ee](?<episode>\d{1,2})",
        RegexOptions.Compiled);

    private static readonly Regex YearRe = new(
        @"\b(?<year>(?:19|20)\d{2})\b",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly bool _configured;
    private readonly string _language;
    private readonly ILogger<TmdbService> _logger;

    public TmdbService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<TmdbService> logger)
    {
        _http = httpClientFactory.CreateClient("tmdb");
        _configured = _http.DefaultRequestHeaders.Authorization is not null;
        _language = configuration["TheMovieDbLanguage"] ?? "en-US";
        _logger = logger;
    }

    // ---------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------

    /// <summary>
    /// Parses the raw release title, queries TMDB and returns the best
    /// match, or <see langword="null"/> if the token is not configured
    /// or no results are found.
    /// </summary>
    public async Task<TmdbMetadata?> LookupAsync(string rawTitle, CancellationToken ct = default)
    {
        if (!_configured)
        {
            _logger.LogDebug("TMDB lookup skipped – TheMovieDbApiAccessToken not configured.");
            return null;
        }

        var (cleanTitle, year, season, episode) = ParseReleaseTitle(rawTitle);
        if (string.IsNullOrWhiteSpace(cleanTitle)) return null;

        _logger.LogInformation(
            "TMDB lookup: title={Title}, year={Year}, S={Season}, E={Episode}",
            cleanTitle, year, season, episode);

        try
        {
            return (season.HasValue && episode.HasValue)
                ? await LookupEpisodeAsync(cleanTitle, season.Value, episode.Value, year, ct)
                : await LookupMovieAsync(cleanTitle, year, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TMDB API call failed for title '{Title}'", cleanTitle);
            return null;
        }
    }

    /// <summary>
    /// Writes a Jellyfin/Kodi-compatible .nfo XML file alongside
    /// <paramref name="videoFilePath"/> (same name, <c>.nfo</c> extension).
    /// </summary>
    public async Task WriteNfoAsync(string videoFilePath, TmdbMetadata meta)
    {
        var nfoPath = Path.ChangeExtension(videoFilePath, ".nfo");

        XDocument doc = meta.Kind == TmdbMediaKind.Movie
            ? BuildMovieNfo(meta)
            : BuildEpisodeNfo(meta);

        using var ms = new MemoryStream();
        doc.Save(ms);
        ms.Position = 0;

        await using var fs = new FileStream(nfoPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await ms.CopyToAsync(fs);
    }

    // ---------------------------------------------------------------
    // Movie lookup
    // ---------------------------------------------------------------

    private async Task<TmdbMetadata?> LookupMovieAsync(string title, int? year, CancellationToken ct)
    {
        var query = Uri.EscapeDataString(title);
        var yearParam = year.HasValue ? $"&year={year.Value}" : "";
        var url = $"{ApiBase}search/movie?query={query}{yearParam}&language={_language}&include_adult=false";

        using var searchDoc = await GetJsonAsync(url, ct);
        if (searchDoc is null) return null;

        var results = searchDoc.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0)
        {
            // If year was specified and gave no results, retry without year constraint
            if (year.HasValue)
            {
                var retryUrl = $"{ApiBase}search/movie?query={query}&language={_language}&include_adult=false";
                using var retryDoc = await GetJsonAsync(retryUrl, ct);
                if (retryDoc is null) return null;
                results = retryDoc.RootElement.GetProperty("results");
                if (results.GetArrayLength() == 0) return null;
                return await BuildMovieMetaAsync(results[0], ct);
            }
            return null;
        }

        return await BuildMovieMetaAsync(results[0], ct);
    }

    private async Task<TmdbMetadata> BuildMovieMetaAsync(JsonElement searchResult, CancellationToken ct)
    {
        int id = searchResult.GetProperty("id").GetInt32();
        var detailUrl = $"{ApiBase}movie/{id}?language={_language}&append_to_response=external_ids";

        using var detailDoc = await GetJsonAsync(detailUrl, ct);
        var d = detailDoc?.RootElement ?? searchResult;

        var genres = new List<string>();
        if (d.TryGetProperty("genres", out var gArr))
            foreach (var g in gArr.EnumerateArray())
                if (g.TryGetProperty("name", out var gn))
                    genres.Add(gn.GetString() ?? "");

        var studios = new List<string>();
        if (d.TryGetProperty("production_companies", out var pc))
            foreach (var c in pc.EnumerateArray())
                if (c.TryGetProperty("name", out var cn))
                    studios.Add(cn.GetString() ?? "");

        string? imdbId = null;
        if (d.TryGetProperty("external_ids", out var extIds) &&
            extIds.TryGetProperty("imdb_id", out var imdbEl))
            imdbId = imdbEl.GetString();

        return new TmdbMetadata
        {
            Kind = TmdbMediaKind.Movie,
            TmdbId = id,
            Title = GetString(d, "title") ?? GetString(searchResult, "title") ?? "",
            OriginalTitle = GetString(d, "original_title") ?? "",
            Overview = GetString(d, "overview") ?? "",
            ReleaseDate = GetString(d, "release_date"),
            VoteAverage = GetDouble(d, "vote_average"),
            VoteCount = GetInt(d, "vote_count"),
            PosterPath = GetString(d, "poster_path"),
            BackdropPath = GetString(d, "backdrop_path"),
            Genres = genres,
            Studios = studios,
            Runtime = TryGetInt(d, "runtime"),
            Tagline = GetString(d, "tagline"),
            ImdbId = imdbId,
        };
    }

    // ---------------------------------------------------------------
    // TV / Episode lookup
    // ---------------------------------------------------------------

    private async Task<TmdbMetadata?> LookupEpisodeAsync(
        string showTitle, int season, int episode, int? year, CancellationToken ct)
    {
        var query = Uri.EscapeDataString(showTitle);
        var yearParam = year.HasValue ? $"&first_air_date_year={year.Value}" : "";
        var url = $"{ApiBase}search/tv?query={query}{yearParam}&language={_language}";

        using var searchDoc = await GetJsonAsync(url, ct);
        if (searchDoc is null) return null;

        var results = searchDoc.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0) return null;

        int showId = results[0].GetProperty("id").GetInt32();
        string showName = GetString(results[0], "name") ?? showTitle;

        var epUrl = $"{ApiBase}tv/{showId}/season/{season}/episode/{episode}?language={_language}";
        using var epDoc = await GetJsonAsync(epUrl, ct);
        if (epDoc is null) return null;

        var ep = epDoc.RootElement;

        return new TmdbMetadata
        {
            Kind = TmdbMediaKind.TvEpisode,
            TmdbId = GetInt(ep, "id"),
            ShowTmdbId = showId,
            ShowTitle = showName,
            Title = showName,
            OriginalTitle = showName,
            Overview = GetString(ep, "overview") ?? "",
            ReleaseDate = GetString(ep, "air_date"),
            VoteAverage = GetDouble(ep, "vote_average"),
            VoteCount = GetInt(ep, "vote_count"),
            PosterPath = GetString(ep, "still_path"),
            SeasonNumber = season,
            EpisodeNumber = episode,
            EpisodeTitle = GetString(ep, "name"),
            AiredDate = GetString(ep, "air_date"),
        };
    }

    // ---------------------------------------------------------------
    // NFO builders
    // ---------------------------------------------------------------

    private static XDocument BuildMovieNfo(TmdbMetadata meta)
    {
        int? year = TryParseYear(meta.ReleaseDate);
        var root = new XElement("movie");

        root.Add(new XElement("title", meta.Title));
        if (!string.IsNullOrWhiteSpace(meta.OriginalTitle) && meta.OriginalTitle != meta.Title)
            root.Add(new XElement("originaltitle", meta.OriginalTitle));
        if (year.HasValue)
            root.Add(new XElement("year", year.Value));
        if (!string.IsNullOrWhiteSpace(meta.Overview))
            root.Add(new XElement("plot", meta.Overview));
        if (!string.IsNullOrWhiteSpace(meta.Tagline))
            root.Add(new XElement("tagline", meta.Tagline));
        if (meta.Runtime.HasValue)
            root.Add(new XElement("runtime", meta.Runtime.Value));

        root.Add(new XElement("rating", meta.VoteAverage.ToString("F1", CultureInfo.InvariantCulture)));
        root.Add(new XElement("votes", meta.VoteCount));

        root.Add(new XElement("uniqueid",
            new XAttribute("type", "tmdb"),
            new XAttribute("default", "true"),
            meta.TmdbId));
        if (!string.IsNullOrWhiteSpace(meta.ImdbId))
            root.Add(new XElement("uniqueid",
                new XAttribute("type", "imdb"),
                meta.ImdbId));

        foreach (var g in meta.Genres)
            root.Add(new XElement("genre", g));
        foreach (var s in meta.Studios)
            root.Add(new XElement("studio", s));

        if (!string.IsNullOrWhiteSpace(meta.PosterPath))
        {
            root.Add(new XElement("thumb",
                new XAttribute("aspect", "poster"),
                $"{ImageBaseUrl}{meta.PosterPath}"));
        }
        if (!string.IsNullOrWhiteSpace(meta.BackdropPath))
        {
            root.Add(new XElement("fanart",
                new XElement("thumb", $"{ImageBaseUrl}{meta.BackdropPath}")));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static XDocument BuildEpisodeNfo(TmdbMetadata meta)
    {
        var root = new XElement("episodedetails");

        root.Add(new XElement("title", meta.EpisodeTitle ?? meta.Title));
        if (!string.IsNullOrWhiteSpace(meta.ShowTitle))
            root.Add(new XElement("showtitle", meta.ShowTitle));
        if (meta.SeasonNumber.HasValue)
            root.Add(new XElement("season", meta.SeasonNumber.Value));
        if (meta.EpisodeNumber.HasValue)
            root.Add(new XElement("episode", meta.EpisodeNumber.Value));
        if (!string.IsNullOrWhiteSpace(meta.Overview))
            root.Add(new XElement("plot", meta.Overview));

        root.Add(new XElement("rating", meta.VoteAverage.ToString("F1", CultureInfo.InvariantCulture)));
        root.Add(new XElement("votes", meta.VoteCount));

        root.Add(new XElement("uniqueid",
            new XAttribute("type", "tmdb"),
            new XAttribute("default", "true"),
            meta.TmdbId));

        if (!string.IsNullOrWhiteSpace(meta.AiredDate))
            root.Add(new XElement("aired", meta.AiredDate));

        if (!string.IsNullOrWhiteSpace(meta.PosterPath))
        {
            root.Add(new XElement("thumb",
                new XAttribute("aspect", "thumb"),
                $"{ImageBaseUrl}{meta.PosterPath}"));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    // ---------------------------------------------------------------
    // Title parser
    // ---------------------------------------------------------------

    internal (string CleanTitle, int? Year, int? Season, int? Episode) ParseReleaseTitle(string raw)
    {
        // Replace dots and underscores with spaces
        string s = raw.Replace('.', ' ').Replace('_', ' ').Trim();

        // Detect TV episode marker (S01E01)
        var epMatch = EpisodeRe.Match(s);
        int? season = null;
        int? episode = null;
        if (epMatch.Success)
        {
            season = int.Parse(epMatch.Groups["season"].Value, CultureInfo.InvariantCulture);
            episode = int.Parse(epMatch.Groups["episode"].Value, CultureInfo.InvariantCulture);
            // Title = everything before the episode marker
            s = s[..epMatch.Index].Trim();
        }
        else
        {
            // Find where release tags start and truncate
            var tagMatch = ReleaseTagRe.Match(s);
            if (tagMatch.Success)
                s = s[..tagMatch.Index].Trim();
        }

        // Extract the first year from the remaining string
        int? year = null;
        var yearMatch = YearRe.Match(s);
        if (yearMatch.Success)
        {
            year = int.Parse(yearMatch.Groups["year"].Value, CultureInfo.InvariantCulture);
            // Remove year from title only if it's at the end (common pattern)
            int yIdx = yearMatch.Index;
            if (yIdx + yearMatch.Length >= s.TrimEnd().Length)
                s = s[..yIdx].Trim();
        }

        // Final cleanup: strip leading/trailing punctuation and collapse spaces
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '.', '_');

        return (s, year, season, episode);
    }

    // ---------------------------------------------------------------
    // HTTP helpers
    // ---------------------------------------------------------------

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("TMDB API returned {Status} for {Url}", response.StatusCode, url);
            return null;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    // ---------------------------------------------------------------
    // JSON element helpers
    // ---------------------------------------------------------------

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;

    private static int? TryGetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static double GetDouble(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : 0.0;

    private static int? TryParseYear(string? date) =>
        date?.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out int y) ? y : null;
}
