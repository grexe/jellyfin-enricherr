using System.Collections.Generic;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Decides whether a probed YouTube video is an acceptable trailer for a movie: duration
/// sanity checks (universal cap, and relative to the movie's own runtime) plus, for
/// search-derived candidates, a title/keyword match. Ported from the standalone script's
/// create_trailer_filter.
/// </summary>
public static class TrailerCandidateFilter
{
    /// <summary>Whether the candidate should be accepted as this movie's trailer.</summary>
    public static bool Accept(
        YtDlpCandidate candidate,
        IReadOnlyList<string> titleVariants,
        double? movieDurationSeconds,
        bool isSearch,
        int maxTrailerDurationSeconds,
        out string? rejectReason)
    {
        var duration = candidate.DurationSeconds;

        if (duration is not null && duration > maxTrailerDurationSeconds)
        {
            rejectReason = $"Duration > {maxTrailerDurationSeconds}s ({duration}s)";
            return false;
        }

        if (movieDurationSeconds is > 60 && duration is not null && duration >= movieDurationSeconds * 0.6)
        {
            rejectReason = $"Too long compared to movie ({duration}s >= {movieDurationSeconds}s)";
            return false;
        }

        if (isSearch)
        {
            var titleMatch = TitleMatching.TitleMatches(candidate.Title, titleVariants);
            if (!titleMatch)
            {
                rejectReason = $"Title mismatch: {candidate.Title}";
                return false;
            }

            var keywordMatch = TitleMatching.HasTrailerKeyword(candidate.Title);
            if (!keywordMatch)
            {
                rejectReason = $"Title matches, but no trailer keyword (trailer/teaser/...): {candidate.Title}";
                return false;
            }
        }

        rejectReason = null;
        return true;
    }
}
