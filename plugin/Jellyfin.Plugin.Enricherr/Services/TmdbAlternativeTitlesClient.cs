using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Fetches a movie's alternate (localized/regional) titles directly from TMDb's REST
/// API (<c>GET /movie/{id}/alternative_titles</c>), using a user-supplied TMDb API key.
/// Needed because Jellyfin's own bundled TMDb integration
/// (<c>MediaBrowser.Providers.Plugins.Tmdb.TmdbClientManager</c>) never requests
/// <c>MovieMethods.AlternativeTitles</c> and doesn't expose its own underlying
/// <c>TMDbClient</c> instance for a plugin to reuse - confirmed by reading Jellyfin's
/// own server source, not just guessed from its public API surface. A separate TMDb
/// API key (free, near-instant approval, generous rate limit) is the only way to reach
/// this endpoint from outside Jellyfin's own code. Only used as a rescue path in
/// <see cref="MissingMetadataMatcher"/>, when a search candidate's own primary title
/// doesn't clear the similarity bar on its own - confirmed live: a German-titled short
/// film ("75 cl Schicksal") search-matched only to its English TMDb entry
/// ("A Bottle of Wishes"), which scored far too low on primary-title similarity alone.
/// </summary>
public class TmdbAlternativeTitlesClient
{
    // Identifies this plugin (and its version) to TMDb's server as a matter of good
    // API citizenship - not required by TMDb, but a legitimate caller should be
    // identifiable in their logs if they ever need to reach out about it.
    private static readonly ProductInfoHeaderValue UserAgent = new(
        "Jellyfin-Plugin-Enricherr",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbAlternativeTitlesClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public TmdbAlternativeTitlesClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches every alternate title TMDb has recorded for the movie with the given
    /// TMDb id, each tagged with its own ISO 3166-1 country code. Empty (never null,
    /// never throws) on a missing key/id or any failure - this is a best-effort rescue
    /// lookup, not a hard dependency the rest of the match should ever fail over.
    /// </summary>
    /// <param name="apiKey">The user's own TMDb API key (v3 auth). Empty/whitespace short-circuits to no results.</param>
    /// <param name="tmdbId">The candidate's TMDb movie id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task<IReadOnlyList<(string CountryCode, string Title)>> GetAlternativeTitlesAsync(string apiKey, string tmdbId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(tmdbId))
        {
            return Array.Empty<(string, string)>();
        }

        var url = $"https://api.themoviedb.org/3/movie/{Uri.EscapeDataString(tmdbId)}/alternative_titles?api_key={Uri.EscapeDataString(apiKey)}";

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(UserAgent);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "  > TMDb alternative titles lookup for id {TmdbId} failed: HTTP {Status}.",
                    tmdbId,
                    (int)response.StatusCode);
                return Array.Empty<(string, string)>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("titles", out var titlesElement) || titlesElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<(string, string)>();
            }

            var result = new List<(string CountryCode, string Title)>();
            foreach (var entry in titlesElement.EnumerateArray())
            {
                var countryCode = entry.TryGetProperty("iso_3166_1", out var cc) ? cc.GetString() : null;
                var title = entry.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (!string.IsNullOrEmpty(countryCode) && !string.IsNullOrEmpty(title))
                {
                    result.Add((countryCode, title));
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Best-effort rescue lookup: any failure here just means this one
            // candidate is judged on its primary title alone, not worth aborting the
            // whole match attempt over - OperationCanceledException deliberately
            // isn't caught here, so an actual run cancellation still propagates.
            _logger.LogWarning("  > TMDb alternative titles lookup for id {TmdbId} failed: {Error}", tmdbId, ex.Message);
            return Array.Empty<(string, string)>();
        }
    }
}
