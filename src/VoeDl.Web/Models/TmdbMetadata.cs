namespace VoeDl.Web.Models;

public enum TmdbMediaKind { Movie, TvEpisode }

/// <summary>
/// Metadata resolved from The Movie Database (TMDB) for a single movie or TV episode.
/// </summary>
public sealed class TmdbMetadata
{
    public TmdbMediaKind Kind { get; init; }

    // ---------------------------------------------------------------
    // Common fields
    // ---------------------------------------------------------------
    public int TmdbId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string OriginalTitle { get; init; } = string.Empty;
    public string Overview { get; init; } = string.Empty;
    public string? ReleaseDate { get; init; }
    public double VoteAverage { get; init; }
    public int VoteCount { get; init; }
    public string? PosterPath { get; init; }
    public string? BackdropPath { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Studios { get; init; } = [];

    // ---------------------------------------------------------------
    // Movie-specific
    // ---------------------------------------------------------------
    public int? Runtime { get; init; }
    public string? Tagline { get; init; }
    public string? ImdbId { get; init; }

    // ---------------------------------------------------------------
    // TV-episode-specific
    // ---------------------------------------------------------------

    /// <summary>TMDB ID of the parent TV show.</summary>
    public int? ShowTmdbId { get; init; }
    public string? ShowTitle { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    /// <summary>Title of the individual episode.</summary>
    public string? EpisodeTitle { get; init; }
    /// <summary>Air date of the individual episode (ISO 8601).</summary>
    public string? AiredDate { get; init; }
}
