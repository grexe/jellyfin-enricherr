using System;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Thrown when yt-dlp reports that YouTube has rate-limited the current session
/// ("This content isn't available, try again later... rate-limited by YouTube for up
/// to an hour"). Continuing to retry with a different client, or continuing on to the
/// next movie/series in a large run, wastes time against a block that won't lift
/// itself any faster and risks making it worse - so this is deliberately a distinct
/// exception type that FetchTrailersTask lets propagate all the way out of the
/// movie/series loop, stopping the whole run early (with whatever was found before it
/// hit, saved the same way a cancelled run's partial summary already is) rather than
/// being caught and retried like an ordinary per-candidate failure.
/// </summary>
public class YouTubeRateLimitedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeRateLimitedException"/> class.
    /// </summary>
    /// <param name="message">The detail message.</param>
    public YouTubeRateLimitedException(string message)
        : base(message)
    {
    }
}
