using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Looks up a movie/series' theme song on <see href="https://github.com/LizardByte/ThemerrDB">ThemerrDB</see> -
/// the same community-curated database the Themerr-jellyfin plugin uses. It's a static,
/// daily-updated JSON file per item keyed by TMDb id (<c>https://app.lizardbyte.dev/ThemerrDB/
/// &lt;movies|tv_shows&gt;/themoviedb/&lt;tmdbId&gt;.json</c>), not a search - so no title/
/// duration/keyword matching is needed here the way <see cref="TrailerCandidateFilter"/>
/// does for trailers, ThemerrDB's own curators already did that. Only the lookup is
/// reused from Themerr; the actual download goes through <see cref="YtDlpClient"/>
/// instead of Themerr's YoutubeExplode-based downloader, which fails outright on
/// plenty of videos yt-dlp handles fine (confirmed live:
/// <c>YoutubeExplode.Exceptions.VideoUnavailableException</c> on a video yt-dlp had no
/// trouble with).
/// </summary>
public class ThemerrDbClient
{
    private const string BaseUrl = "https://app.lizardbyte.dev/ThemerrDB";

    // Identifies this plugin (and its version) to ThemerrDB's server as a matter of
    // good API citizenship, since it's a community-run, no-API-key public data host -
    // not required by anything ThemerrDB itself documents, but a legitimate caller
    // should be identifiable in their logs if they ever need to reach out about it.
    private static readonly ProductInfoHeaderValue UserAgent = new(
        "Jellyfin-Plugin-Enricherr",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThemerrDbClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public ThemerrDbClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Looks up the theme song YouTube URL for a movie, by TMDb id. Null if none is known.</summary>
    public Task<string?> GetMovieThemeUrlAsync(string tmdbId, CancellationToken cancellationToken) =>
        GetThemeUrlAsync($"{BaseUrl}/movies/themoviedb/{tmdbId}.json", cancellationToken);

    /// <summary>Looks up the theme song YouTube URL for a TV series, by TMDb id. Null if none is known.</summary>
    public Task<string?> GetSeriesThemeUrlAsync(string tmdbId, CancellationToken cancellationToken) =>
        GetThemeUrlAsync($"{BaseUrl}/tv_shows/themoviedb/{tmdbId}.json", cancellationToken);

    private async Task<string?> GetThemeUrlAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(UserAgent);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // No entry for this item in ThemerrDB - normal and common, not an error.
                return null;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("youtube_theme_url", out var value) ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Best-effort lookup: any failure here (network hiccup, unexpected response
            // shape) just means "no theme song this run" for this one item, not worth
            // aborting anything over - OperationCanceledException deliberately isn't
            // caught here, so an actual run cancellation still propagates normally.
            _logger.LogWarning("  > Could not look up theme song on ThemerrDB ({Url}): {Error}", url, ex.Message);
            return null;
        }
    }
}
