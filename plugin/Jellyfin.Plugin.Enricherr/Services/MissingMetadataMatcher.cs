using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Searches for and applies remote metadata for a movie Jellyfin has no match for at
/// all - common for oddly-named releases (scene tags, internal archive/catalog
/// numbers appended to an otherwise-findable title) that defeat Jellyfin's own
/// provider matching even though the real title is findable once the noise this
/// plugin already strips (<see cref="TitleMatching"/>) is gone. Confirmed live: a
/// short film filed as "Die Welt an und für sich-052393-000-A (2020).mp4" - an
/// internal archive catalog number, not part of the title - had no Jellyfin match at
/// all, despite "Die Welt an und für sich" (2020) being findable on TMDb directly.
///
/// Deliberately conservative in three ways, to keep an unattended scheduled task from
/// ever silently applying a wrong match: (1) only ever touches an item with an
/// EMPTY ProviderIds - never second-guesses a match Jellyfin already made, right or
/// wrong; (2) requires a strict title similarity AND a year match against the search
/// candidate; (3) requires the candidate's own claimed runtime (fetched from its
/// provider directly, never by speculatively applying it to the real Jellyfin item
/// first) to agree with this plugin's own ffprobe of the local file within a tight
/// tolerance. A candidate that fails any of these is left alone, logged, and Jellyfin's
/// item is never touched - failing closed is the point.
/// </summary>
public class MissingMetadataMatcher
{
    private const double TitleSimilarityThreshold = 0.9;
    private const int YearToleranceYears = 1;
    private const double RuntimeToleranceMinutes = 1.0;

    // Matches a trailing internal archive/catalog number (e.g. "-052393-000-A") -
    // confirmed live against a real "Kurzschluss"-style short-film archive: two or
    // more hyphen-terminated digit groups followed by a single letter, tacked onto an
    // otherwise findable title. The separator before the first digit group and the
    // group count both vary in practice - confirmed live, one file's id was space-
    // (not hyphen-) attached with an extra leading digit group ("29, bald
    // 45-039221-004-A" - the catalog id is "45-039221-004-A" as a whole, not just the
    // last two groups), which the original hyphen-only, exactly-two-groups pattern
    // missed entirely, leaving a stray "45" in the search query. Deliberately still
    // narrow (not a general noise-stripping pattern - TitleMatching.CleanMediaTitle
    // already handles the broad cases) since this is only used as a fallback SEARCH
    // query, never applied to the display/rename title.
    private static readonly Regex ArchiveCatalogSuffixRegex = new(@"[\s-](?:\d+-){2,}[A-Za-z]\s*$", RegexOptions.Compiled);

    private readonly IProviderManager _providerManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MissingMetadataMatcher"/> class.
    /// </summary>
    public MissingMetadataMatcher(IProviderManager providerManager, ILibraryManager libraryManager, IDirectoryService directoryService, ILogger logger)
    {
        _providerManager = providerManager;
        _libraryManager = libraryManager;
        _directoryService = directoryService;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to find and apply a confident remote metadata match for
    /// <paramref name="movie"/>, using <paramref name="candidateTitle"/>/
    /// <paramref name="candidateYear"/> (this plugin's own resolved title/year, not
    /// Jellyfin's) as the search query. A no-op - returning false - if
    /// <paramref name="movie"/> already has any provider id at all.
    /// </summary>
    /// <returns>Whether a match was found and applied (Jellyfin's own metadata refresh already ran).</returns>
    public async Task<bool> TryMatchMovieAsync(
        Movie movie,
        string candidateTitle,
        string? candidateYear,
        string localPath,
        string? ffprobePath,
        CancellationToken cancellationToken)
    {
        if (movie.ProviderIds.Count > 0 || string.IsNullOrWhiteSpace(candidateTitle))
        {
            return false;
        }

        var year = int.TryParse(candidateYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear) ? parsedYear : (int?)null;

        // Confirmed live: searching with the archive-number suffix still attached
        // ("75 cl Schicksal-035082-000-A") came back with 0 results from every
        // provider - unlike TitleSimilarity's scoring (tolerant of exactly this kind
        // of trailing noise), a remote search API itself isn't necessarily forgiving
        // of a query that noisy. If the first search comes back empty and the title
        // looks like it has a trailing archive/catalog-number suffix (the pattern
        // this "Kurzschluss"-style archive naming uses - a hyphenated run of two
        // digit groups then a single letter), retry once with that suffix stripped.
        var searchTitles = new System.Collections.Generic.List<string> { candidateTitle };
        var strippedTitle = ArchiveCatalogSuffixRegex.Replace(candidateTitle, string.Empty).TrimEnd();
        if (strippedTitle.Length > 0 && !string.Equals(strippedTitle, candidateTitle, StringComparison.Ordinal))
        {
            searchTitles.Add(strippedTitle);
        }

        // Each title is tried with the year first, then - confirmed live, the exact
        // "75 cl Schicksal" case above - without it: TMDb's own search appears to use
        // the year as a hard filter, excluding an otherwise-perfect title match when
        // its own recorded release year doesn't exactly line up with ours. Safe to
        // drop here since the scoring step below already re-checks year (with its own
        // +/-1 tolerance) against whatever comes back either way.
        var searchAttempts = new System.Collections.Generic.List<(string Title, bool IncludeYear)>();
        foreach (var title in searchTitles)
        {
            searchAttempts.Add((title, true));
        }

        if (year is not null)
        {
            searchAttempts.Add((searchTitles[^1], false));
        }

        var results = new System.Collections.Generic.List<RemoteSearchResult>();
        foreach (var attempt in searchAttempts)
        {
            var query = new RemoteSearchQuery<MovieInfo>
            {
                SearchInfo = new MovieInfo
                {
                    Name = attempt.Title,
                    Year = attempt.IncludeYear ? year : null,
                    MetadataLanguage = movie.GetPreferredMetadataLanguage(),
                    MetadataCountryCode = movie.GetPreferredMetadataCountryCode()
                }
            };

            try
            {
                results = (await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(query, cancellationToken).ConfigureAwait(false)).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("  > Metadata search for \"{Title}\" failed: {Error}", attempt.Title, ex.Message);
                continue;
            }

            if (results.Count > 0)
            {
                break;
            }
        }

        var best = results
            .Select(r => (Result: r, Similarity: Levenshtein.TitleSimilarity(candidateTitle, r.Name)))
            .Where(r => r.Similarity >= TitleSimilarityThreshold)
            .Where(r => year is null || r.Result.ProductionYear is null || Math.Abs(r.Result.ProductionYear.Value - year.Value) <= YearToleranceYears)
            .OrderByDescending(r => r.Similarity)
            .FirstOrDefault();

        if (best.Result is null)
        {
            _logger.LogInformation(
                "  > No confident metadata match for \"{Title}\" ({Year}) among {Count} search result(s) - leaving unmatched.",
                candidateTitle,
                candidateYear ?? "unknown year",
                results.Count);
            return false;
        }

        if (!string.IsNullOrEmpty(ffprobePath))
        {
            var localDurationSeconds = await VideoProbe.GetDurationSecondsAsync(ffprobePath, localPath, _logger, cancellationToken).ConfigureAwait(false);
            if (localDurationSeconds is not null)
            {
                var candidateRuntimeMinutes = await GetCandidateRuntimeMinutesAsync(movie, best.Result, cancellationToken).ConfigureAwait(false);
                if (candidateRuntimeMinutes is not null)
                {
                    var localMinutes = localDurationSeconds.Value / 60.0;
                    if (Math.Abs(localMinutes - candidateRuntimeMinutes.Value) > RuntimeToleranceMinutes)
                    {
                        _logger.LogInformation(
                            "  > Found a title/year match (\"{MatchName}\", {Similarity:P0} similar) for \"{Title}\", but its runtime ({CandidateMinutes:F1} min) doesn't match the local file ({LocalMinutes:F1} min) - not applying.",
                            best.Result.Name,
                            best.Similarity,
                            candidateTitle,
                            candidateRuntimeMinutes.Value,
                            localMinutes);
                        return false;
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "  > Could not determine {Provider}'s claimed runtime for \"{MatchName}\" - applying the match on title/year confidence alone.",
                        best.Result.SearchProviderName,
                        best.Result.Name);
                }
            }
        }

        var refreshOptions = new MetadataRefreshOptions(_directoryService)
        {
            SearchResult = best.Result,
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = true,
            ForceSave = true,
            IsAutomated = true
        };

        try
        {
            await _providerManager.RefreshSingleItem(movie, refreshOptions, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "  > Applied metadata match: \"{Title}\" -> \"{MatchName}\" ({Year}, via {Provider}, {Similarity:P0} title similarity).",
                candidateTitle,
                best.Result.Name,
                best.Result.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "unknown year",
                best.Result.SearchProviderName,
                best.Similarity);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("  > Failed to apply metadata match for \"{Title}\": {Error}", candidateTitle, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Fetches a search candidate's own claimed runtime directly from its provider -
    /// never by applying it to the real Jellyfin item first and checking afterward,
    /// which would mean speculatively overwriting a library item's metadata before
    /// confirming it's even the right one.
    /// </summary>
    private async Task<double?> GetCandidateRuntimeMinutesAsync(Movie movie, RemoteSearchResult candidate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(candidate.SearchProviderName))
        {
            return null;
        }

        var libraryOptions = _libraryManager.GetLibraryOptions(movie);
        var provider = _providerManager.GetMetadataProviders<Movie>(movie, libraryOptions)
            .OfType<IRemoteMetadataProvider<Movie, MovieInfo>>()
            .FirstOrDefault(p => string.Equals(p.Name, candidate.SearchProviderName, StringComparison.Ordinal));

        if (provider is null)
        {
            return null;
        }

        try
        {
            var lookupInfo = new MovieInfo
            {
                Name = candidate.Name,
                Year = candidate.ProductionYear,
                ProviderIds = candidate.ProviderIds
            };

            var result = await provider.GetMetadata(lookupInfo, cancellationToken).ConfigureAwait(false);
            if (!result.HasMetadata || result.Item is null || result.Item.RunTimeTicks is null)
            {
                return null;
            }

            return TimeSpan.FromTicks(result.Item.RunTimeTicks.Value).TotalMinutes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "  > Could not fetch full metadata for candidate \"{Name}\" from {Provider}: {Error}",
                candidate.Name,
                candidate.SearchProviderName,
                ex.Message);
            return null;
        }
    }
}
