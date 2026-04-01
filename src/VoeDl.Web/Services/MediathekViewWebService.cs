using System.Xml.Linq;
using Microsoft.AspNetCore.WebUtilities;

namespace VoeDl.Web.Services;

public sealed record MediathekViewWebResult(
    string Title,
    string MediaUrl,
    string? Category,
    string? Creator,
    DateTimeOffset? PublishedAt,
    string? PageUrl);

public sealed class MediathekViewWebService
{
    private const string BaseUrl = "https://mediathekviewweb.de";
    private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";

    private readonly HttpClient _http;
    private readonly ILogger<MediathekViewWebService> _logger;

    public MediathekViewWebService(IHttpClientFactory httpClientFactory, ILogger<MediathekViewWebService> logger)
    {
        _http = httpClientFactory.CreateClient("mediathek");
        _logger = logger;
    }

    public async Task<IReadOnlyList<MediathekViewWebResult>> SearchAsync(
        string query,
        bool everywhere = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var feedUrl = BuildFeedUrl(query, everywhere);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = XDocument.Load(contentStream);
            return ParseFeed(document);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch MediathekViewWeb search feed for query '{Query}'", query);
            return [];
        }
    }

    public static bool TryParseSearchInput(string input, out string query, out bool everywhere)
    {
        query = string.Empty;
        everywhere = true;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        if (trimmed.StartsWith("mvw:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("mediathek:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed.Split(':', 2);
            if (parts.Length < 2)
                return false;

            query = parts[1].Trim();
            return !string.IsNullOrWhiteSpace(query);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.EndsWith("mediathekviewweb.de", StringComparison.OrdinalIgnoreCase))
            return false;

        if (uri.AbsolutePath.Equals("/feed", StringComparison.OrdinalIgnoreCase))
        {
            var queryParams = QueryHelpers.ParseQuery(uri.Query);
            if (!queryParams.TryGetValue("query", out var values))
                return false;

            query = values.ToString();
            if (string.IsNullOrWhiteSpace(query))
                return false;

            if (queryParams.TryGetValue("everywhere", out var everywhereValues))
                everywhere = everywhereValues.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

            return true;
        }

        if (uri.AbsolutePath.Equals("/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(uri.Fragment))
        {
            var fragment = uri.Fragment.TrimStart('#');
            var fragmentParams = QueryHelpers.ParseQuery("?" + fragment);
            if (!fragmentParams.TryGetValue("query", out var values))
                return false;

            query = values.ToString();
            if (string.IsNullOrWhiteSpace(query))
                return false;

            if (fragmentParams.TryGetValue("everywhere", out var everywhereValues))
                everywhere = everywhereValues.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

            return true;
        }

        return false;
    }

    private static string BuildFeedUrl(string query, bool everywhere) =>
        $"{BaseUrl}/feed?query={Uri.EscapeDataString(query)}&everywhere={everywhere.ToString().ToLowerInvariant()}";

    private static IReadOnlyList<MediathekViewWebResult> ParseFeed(XDocument document)
    {
        var channel = document.Root?.Element("channel");
        if (channel is null)
            return [];

        var results = new List<MediathekViewWebResult>();
        foreach (var item in channel.Elements("item"))
        {
            var title = item.Element("title")?.Value?.Trim() ?? string.Empty;
            var link = item.Element("link")?.Value?.Trim();
            var category = item.Element("category")?.Value?.Trim();
            var creator = item.Element(DcNamespace + "creator")?.Value?.Trim();
            var pubDateText = item.Element("pubDate")?.Value?.Trim();
            var publishedAt = DateTimeOffset.TryParse(pubDateText, out var parsedDate)
                ? parsedDate
                : null as DateTimeOffset?;

            var enclosure = item.Element("enclosure");
            var mediaUrl = enclosure?.Attribute("url")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(mediaUrl))
                mediaUrl = link;

            if (string.IsNullOrWhiteSpace(mediaUrl))
                continue;

            results.Add(new MediathekViewWebResult(
                title,
                mediaUrl!,
                category,
                creator,
                publishedAt,
                link));
        }

        return results;
    }
}
