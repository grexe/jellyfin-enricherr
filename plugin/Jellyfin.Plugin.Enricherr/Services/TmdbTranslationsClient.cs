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
/// Fetches a movie's localized titles directly from TMDb's REST API
/// (<c>GET /movie/{id}/translations</c>), using a user-supplied TMDb credential.
/// Needed because Jellyfin's own bundled TMDb integration
/// (<c>MediaBrowser.Providers.Plugins.Tmdb.TmdbClientManager</c>) never requests this
/// data and doesn't expose its own underlying <c>TMDbClient</c> instance for a plugin
/// to reuse - confirmed by reading Jellyfin's own server source, not just guessed from
/// its public API surface. Deliberately NOT <c>/movie/{id}/alternative_titles</c> (a
/// separate, curated AKA list) - confirmed live/directly against TMDb: the exact case
/// motivating this ("75 cl Schicksal", a German TV broadcast title for the French
/// short "75 centilitres de prière"/"A Bottle of Wishes") has zero entries in that
/// list at all ("Es wurden keine Alternativtitel hinzugefügt"), but IS present as this
/// movie's German (de-DE) *translation* - a different, far more commonly populated
/// TMDb dataset (every language TMDb has a community-contributed localization for, not
/// just curated regional release names). Only used as a rescue path in
/// <see cref="MissingMetadataMatcher"/>, when a search candidate's own primary title
/// doesn't clear the similarity bar on its own.
/// </summary>
public class TmdbTranslationsClient
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
    /// Initializes a new instance of the <see cref="TmdbTranslationsClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public TmdbTranslationsClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches every localized title TMDb has recorded for the movie with the given
    /// TMDb id, each tagged with its own ISO 639-1 language and ISO 3166-1 country
    /// code. A language with no title override (TMDb still lists the language but
    /// leaves the title empty, meaning "same as the primary title") is skipped - never
    /// worth scoring against, and would only ever come out equal to what the primary-
    /// title check already tried. Empty (never null, never throws) on a missing
    /// key/id or any failure - this is a best-effort rescue lookup, not a hard
    /// dependency the rest of the match should ever fail over.
    /// </summary>
    /// <param name="apiKey">
    /// The user's own TMDb credential - either the long "API Read Access Token" (v4
    /// auth, a JWT, sent as a Bearer token - the one TMDb's own current API docs use)
    /// or the older "API Key" (v3 auth, sent as the <c>api_key</c> query parameter)
    /// from https://www.themoviedb.org/settings/api - TMDb's settings page presents
    /// both side by side under easily-confused names, so rather than silently 401ing
    /// when the "wrong" one of the two is pasted in, both are recognized and used
    /// correctly. Empty/whitespace short-circuits to no results.
    /// </param>
    /// <param name="tmdbId">The candidate's TMDb movie id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task<IReadOnlyList<(string LanguageCode, string CountryCode, string Title)>> GetTranslatedTitlesAsync(string apiKey, string tmdbId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(tmdbId))
        {
            return Array.Empty<(string, string, string)>();
        }

        // A v4 Read Access Token is a JWT (three dot-separated base64url segments,
        // well over 100 characters); a v3 API key is a plain 32-character hex string.
        // Long enough and dot-bearing is enough to tell them apart reliably without
        // needing a full JWT parse.
        var isV4Token = apiKey.Length > 100 && apiKey.Contains('.', StringComparison.Ordinal);

        var url = isV4Token
            ? $"https://api.themoviedb.org/3/movie/{Uri.EscapeDataString(tmdbId)}/translations"
            : $"https://api.themoviedb.org/3/movie/{Uri.EscapeDataString(tmdbId)}/translations?api_key={Uri.EscapeDataString(apiKey)}";

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(UserAgent);
            if (isV4Token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "  > TMDb translations lookup for id {TmdbId} failed: HTTP {Status}{Hint}.",
                    tmdbId,
                    (int)response.StatusCode,
                    response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? " - check the TMDb API key setting is correct and hasn't been revoked"
                        : string.Empty);
                return Array.Empty<(string, string, string)>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("translations", out var translationsElement) || translationsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<(string, string, string)>();
            }

            var result = new List<(string LanguageCode, string CountryCode, string Title)>();
            foreach (var entry in translationsElement.EnumerateArray())
            {
                var languageCode = entry.TryGetProperty("iso_639_1", out var lc) ? lc.GetString() : null;
                var countryCode = entry.TryGetProperty("iso_3166_1", out var cc) ? cc.GetString() : null;
                var title = entry.TryGetProperty("data", out var data) && data.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (!string.IsNullOrEmpty(languageCode) && !string.IsNullOrEmpty(title))
                {
                    result.Add((languageCode, countryCode ?? string.Empty, title));
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
            _logger.LogWarning("  > TMDb translations lookup for id {TmdbId} failed: {Error}", tmdbId, ex.Message);
            return Array.Empty<(string, string, string)>();
        }
    }
}
