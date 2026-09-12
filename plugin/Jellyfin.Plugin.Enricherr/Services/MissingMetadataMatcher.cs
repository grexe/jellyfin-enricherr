using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
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
/// Candidate selection is deliberately prioritized title &gt; duration &gt; year, not the
/// reverse: a search result's own claimed release year turned out to be an
/// unreliable signal in practice (confirmed live: "75 cl Schicksal" (1994), filed
/// under a 2020 archival/broadcast date that isn't the film's actual release year at
/// all), while the local file's own runtime (via ffprobe) is a strong, independent
/// corroboration a wrong title match is very unlikely to also happen to satisfy. Title
/// matching also checks a candidate's own TMDb translations
/// (<see cref="TmdbTranslationsClient"/>), not just its primary title - the same
/// "75 cl Schicksal" case is that film's German TMDb *translation*, not a curated
/// "alternative title" (TMDb has none on file for it at all) - its primary/English
/// title ("A Bottle of Wishes") scored far too low against on its own.
///
/// Every candidate is checked against its translations unconditionally, not only as
/// a last resort once the primary title alone finds nothing - confirmed live why
/// that distinction matters: a search for "Der Briefwechsel" (a German filename)
/// matched TWO entirely different, unrelated films sharing that exact generic
/// German phrase - "The Letter Room" (2020, TMDb, whose own German TMDb translation
/// is literally "Der Briefwechsel") and an unrelated 2010 Austrian TV documentary
/// about Thomas Bernhard's letters, via the far less rigorously curated "The Open
/// Movie Database". The OMDb candidate's PRIMARY title happened to match exactly,
/// which alone would already clear the threshold - so checking translations only
/// "as a last resort" would never even look at TMDb's candidate, silently preferring
/// the coincidental, less-trustworthy exact match over the real one. When multiple
/// candidates all end up tied at an exact title match, the TMDb-sourced one is
/// preferred - it's both the source curated well enough to trust here, and the only
/// one this plugin can independently cross-check via translations at all.
///
/// Deliberately conservative regardless: (1) only ever touches an item with an EMPTY
/// ProviderIds - never second-guesses a match Jellyfin already made, right or wrong;
/// (2) always requires a strict title similarity match (primary or localized title)
/// against the search candidate; (3) requires the candidate's own claimed runtime
/// (fetched from its provider directly, never by speculatively applying it to the
/// real Jellyfin item first) to agree with this plugin's own ffprobe of the local
/// file within a tight tolerance, WHENEVER that comparison is actually possible - an
/// available, disagreeing runtime rejects a candidate no matter how exact its title
/// match is, since even that can coincidentally be the wrong movie (confirmed live:
/// two entirely unrelated films can share the exact same generic title); (4) only
/// when a runtime comparison genuinely isn't possible at all does an exact (1.0-scored)
/// title match get accepted on that alone, or - anything less than exact - its year
/// checked as a softer tie-breaker instead. A candidate that fails all of this is left
/// alone, logged, and Jellyfin's item is never touched - failing closed is the point.
/// </summary>
public class MissingMetadataMatcher
{
    private const double TitleSimilarityThreshold = 0.9;

    // Levenshtein.TitleSimilarity returns the literal double 1.0 for both a true
    // exact (case-insensitive) match and its word-boundary-prefix case - never a
    // near-1.0 value from floating-point rounding - so this equality check is exact,
    // not an approximation. Only ever used as a stand-in for corroboration that
    // ISN'T AVAILABLE (no local ffprobe result, or the candidate's provider reports
    // no runtime) - confirmed live requiring corroboration that can't even be
    // fetched only produces false negatives (a candidate's provider entry can simply
    // have no RunTimeTicks recorded, and its own claimed release year can be an
    // unrelated archival/broadcast date). This must NEVER override a corroboration
    // signal that IS available and disagrees, though: also confirmed live, an exact
    // title match can still be the wrong movie entirely when two unrelated films
    // happen to share the exact same generic title from different providers ("Der
    // Briefwechsel" matched both a real film via its TMDb translation and an
    // unrelated, differently-run-timed Austrian TV documentary via a plain string
    // match on a less-curated provider) - an available, disagreeing runtime rejects
    // a candidate regardless of how exact its title match is.
    private const double ExactTitleSimilarity = 1.0;
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
    private readonly TmdbTranslationsClient _tmdbTranslationsClient;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MissingMetadataMatcher"/> class.
    /// </summary>
    public MissingMetadataMatcher(
        IProviderManager providerManager,
        ILibraryManager libraryManager,
        IDirectoryService directoryService,
        IHttpClientFactory httpClientFactory,
        ILogger logger)
    {
        _providerManager = providerManager;
        _libraryManager = libraryManager;
        _directoryService = directoryService;
        _tmdbTranslationsClient = new TmdbTranslationsClient(httpClientFactory, logger);
        _logger = logger;
    }

    /// <summary>
    /// Attempts to find and apply a confident remote metadata match for
    /// <paramref name="movie"/>, using <paramref name="candidateTitle"/>/
    /// <paramref name="candidateYear"/> (this plugin's own resolved title/year, not
    /// Jellyfin's) as the search query. A no-op - returning false - if
    /// <paramref name="movie"/> already has any provider id at all.
    /// </summary>
    /// <param name="movie">The Jellyfin movie item with no existing metadata match.</param>
    /// <param name="candidateTitle">This plugin's own resolved title to search with.</param>
    /// <param name="candidateYear">This plugin's own resolved year, if any.</param>
    /// <param name="localPath">Path to the local video file, for the ffprobe runtime cross-check.</param>
    /// <param name="ffprobePath">Path to Jellyfin's own ffprobe binary, or null if unavailable.</param>
    /// <param name="tmdbApiKey">A user-supplied TMDb credential (API Read Access Token or API Key) for localized-title lookups, or empty to skip them.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether a match was found and applied (Jellyfin's own metadata refresh already ran).</returns>
    public async Task<bool> TryMatchMovieAsync(
        Movie movie,
        string candidateTitle,
        string? candidateYear,
        string localPath,
        string? ffprobePath,
        string? tmdbApiKey,
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
        var searchTitles = new List<string> { candidateTitle };
        var strippedTitle = ArchiveCatalogSuffixRegex.Replace(candidateTitle, string.Empty).TrimEnd();
        if (strippedTitle.Length > 0 && !string.Equals(strippedTitle, candidateTitle, StringComparison.Ordinal))
        {
            searchTitles.Add(strippedTitle);
        }

        // Each title is tried with the year first, then - confirmed live, the exact
        // "75 cl Schicksal" case above - without it: TMDb's own search appears to use
        // the year as a hard filter, excluding an otherwise-perfect title match when
        // its own recorded release year doesn't exactly line up with ours. Safe to
        // drop here since candidate selection below no longer hard-filters on year
        // either - it's checked only as a fallback tie-breaker when runtime can't be.
        var searchAttempts = new List<(string Title, bool IncludeYear)>();
        foreach (var title in searchTitles)
        {
            searchAttempts.Add((title, true));
        }

        if (year is not null)
        {
            searchAttempts.Add((searchTitles[^1], false));
        }

        // Every attempt is tried and pooled, rather than stopping at the first one
        // that returns anything - confirmed live: a year-filtered search can come
        // back with exactly one (wrong, or merely below threshold) candidate, which
        // would otherwise stop the loop before the broader/year-less attempt - the
        // one actually likely to surface the real match - ever runs. Deduplicated by
        // name+year, since the same real candidate often reappears across attempts.
        var results = new List<RemoteSearchResult>();
        var seenCandidates = new HashSet<string>(StringComparer.Ordinal);
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

            var attemptYearLabel = attempt.IncludeYear && year is not null ? year.Value.ToString(CultureInfo.InvariantCulture) : "no year";

            List<RemoteSearchResult> attemptResults;
            try
            {
                attemptResults = (await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(query, cancellationToken).ConfigureAwait(false)).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("  > Metadata search for {Title} ({Year}) failed: {Error}", attempt.Title, attemptYearLabel, ex.Message);
                continue;
            }

            _logger.LogInformation(
                "  > Searching metadata for {Title} ({Year}) -> {Count} result(s): {Results}.",
                attempt.Title,
                attemptYearLabel,
                attemptResults.Count,
                attemptResults.Count == 0
                    ? "none"
                    : string.Join(", ", attemptResults.Select(r => $"{r.Name} ({r.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "unknown year"}, via {r.SearchProviderName})")));

            foreach (var r in attemptResults)
            {
                if (seenCandidates.Add(r.Name + "|" + r.ProductionYear))
                {
                    results.Add(r);
                }
            }
        }

        // Every candidate is scored against its own localized titles too, not just
        // its primary one, and unconditionally - not only as a last resort once the
        // primary title alone has already failed to find anything. Confirmed live
        // why that matters: "Der Briefwechsel" (a German filename) matched TWO
        // entirely different, unrelated films that happen to share the exact same
        // generic German title - "The Letter Room" (2020, TMDb, whose OWN German
        // TMDb translation is literally "Der Briefwechsel") and an unrelated 2010
        // Austrian TV documentary about Thomas Bernhard's letters (via the much less
        // rigorously curated "The Open Movie Database"). The OMDb candidate's
        // PRIMARY title happened to match exactly, so it alone would already clear
        // the threshold - meaning the old "only check translations once primary
        // finds nothing" rule would never even look at TMDb's candidate, silently
        // preferring the coincidental, less-trustworthy exact match. Scoring every
        // candidate's translations up front is the only way both candidates end up
        // properly compared.
        var scoredCandidates = !string.IsNullOrWhiteSpace(tmdbApiKey)
            ? await ScoreCandidatesAsync(candidateTitle, results, tmdbApiKey, cancellationToken).ConfigureAwait(false)
            : results.Select(r => (Result: r, Similarity: Levenshtein.TitleSimilarity(candidateTitle, r.Name), MatchedTitle: (string?)null)).ToList();
        scoredCandidates = scoredCandidates.OrderByDescending(r => r.Similarity).ToList();

        var candidatesForSelection = scoredCandidates
            .Where(r => r.Similarity >= TitleSimilarityThreshold)
            .ToList();

        if (candidatesForSelection.Count == 0)
        {
            var closest = scoredCandidates.FirstOrDefault();
            if (closest.Result is null)
            {
                _logger.LogInformation(
                    "  > No confident metadata match for {Title} ({Year}) - every search came back empty - leaving unmatched.",
                    candidateTitle,
                    candidateYear ?? "unknown year");
            }
            else
            {
                _logger.LogInformation(
                    "  > No confident metadata match for {Title} ({Year}) among {Count} candidate(s) - closest was {ClosestName} ({ClosestYear}, via {Provider}, {Similarity:P0} title similarity) - leaving unmatched.",
                    candidateTitle,
                    candidateYear ?? "unknown year",
                    results.Count,
                    closest.Result.Name,
                    closest.Result.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "unknown year",
                    closest.Result.SearchProviderName,
                    closest.Similarity);
            }

            return false;
        }

        // When several candidates ALL claim an exact title match, prefer the one
        // TheMovieDb itself is the source of over any other provider - confirmed
        // live (see above) that two unrelated films can tie here, and TMDb is both
        // the source curated well enough for this plugin to trust it, and the only
        // one this plugin can independently cross-check via translations at all
        // (an OMDb/TVDB exact match has no such corroboration behind it whatsoever).
        var exactMatches = candidatesForSelection.Where(c => c.Similarity >= ExactTitleSimilarity).ToList();
        if (exactMatches.Count > 1 && !string.Equals(exactMatches[0].Result.SearchProviderName, "TheMovieDb", StringComparison.Ordinal))
        {
            var preferredTmdb = exactMatches.FirstOrDefault(c => string.Equals(c.Result.SearchProviderName, "TheMovieDb", StringComparison.Ordinal));
            if (preferredTmdb.Result is not null)
            {
                _logger.LogInformation(
                    "  > {Count} candidates all matched {Title}'s title exactly ({Names}) - preferring the TheMovieDb one, since only it can be cross-checked via translations.",
                    exactMatches.Count,
                    candidateTitle,
                    string.Join(", ", exactMatches.Select(c => $"{c.Result.Name} ({c.Result.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "unknown year"}, via {c.Result.SearchProviderName})")));
                candidatesForSelection = new List<(RemoteSearchResult Result, double Similarity, string? MatchedTitle)> { preferredTmdb }
                    .Concat(candidatesForSelection.Where(c => !ReferenceEquals(c.Result, preferredTmdb.Result)))
                    .ToList();
            }
        }

        double? localDurationSeconds = null;
        if (string.IsNullOrEmpty(ffprobePath))
        {
            _logger.LogInformation("  > No ffprobe path available - duration can't be cross-checked for {Title}, falling back to year alone.", candidateTitle);
        }
        else
        {
            localDurationSeconds = await VideoProbe.GetDurationSecondsAsync(ffprobePath, localPath, _logger, cancellationToken).ConfigureAwait(false);
        }

        (RemoteSearchResult Result, double Similarity, string? MatchedTitle)? best = null;
        string? acceptedVia = null;
        foreach (var candidate in candidatesForSelection)
        {
            double? candidateRuntimeMinutes = localDurationSeconds is not null
                ? await GetCandidateRuntimeMinutesAsync(movie, candidate.Result, cancellationToken).ConfigureAwait(false)
                : null;

            if (candidateRuntimeMinutes is not null)
            {
                var localMinutes = localDurationSeconds!.Value / 60.0;
                if (Math.Abs(localMinutes - candidateRuntimeMinutes.Value) <= RuntimeToleranceMinutes)
                {
                    best = candidate;
                    acceptedVia = "duration";
                    break;
                }

                // An available, disagreeing runtime rejects this candidate regardless
                // of title similarity - even an exact (1.0) one. Confirmed live an
                // exact title match can still be the wrong movie entirely (two
                // unrelated films sharing a generic title, "Der Briefwechsel") - a
                // real, independently-fetched runtime that disagrees is exactly the
                // corroboration that catches that, and title similarity alone must
                // never override a signal that's actually available and disagrees.
                // The exact-match shortcut below only ever stands in for
                // corroboration that ISN'T available, never overrides one that is.
                _logger.LogInformation(
                    "  > {MatchName} matched {Title}'s title ({Similarity:P0} similar), but its runtime ({CandidateMinutes:F1} min) doesn't match the local file ({LocalMinutes:F1} min) - trying the next candidate, if any.",
                    candidate.Result.Name,
                    candidateTitle,
                    candidate.Similarity,
                    candidateRuntimeMinutes.Value,
                    localMinutes);
                continue;
            }

            // Runtime couldn't be compared (no local ffprobe result, or the provider
            // doesn't report one for this candidate). An exact title match is
            // accepted on that alone here - confirmed live that requiring a
            // corroboration signal that isn't even available only produces false
            // negatives - but anything less than exact still falls back to year as a
            // softer tie-breaker rather than rejecting outright on title alone.
            if (candidate.Similarity >= ExactTitleSimilarity)
            {
                best = candidate;
                acceptedVia = "an exact title match (no runtime available to cross-check)";
                break;
            }

            var yearOk = year is null || candidate.Result.ProductionYear is null || Math.Abs(candidate.Result.ProductionYear.Value - year.Value) <= YearToleranceYears;
            if (yearOk)
            {
                best = candidate;
                acceptedVia = localDurationSeconds is null ? "title alone (no local runtime available)" : "title + year (candidate reported no runtime)";
                break;
            }

            _logger.LogInformation(
                "  > {MatchName} matched {Title}'s title ({Similarity:P0} similar), but its year ({CandidateYear}) doesn't match ({Year}) and no runtime could be compared - trying the next candidate, if any.",
                candidate.Result.Name,
                candidateTitle,
                candidate.Similarity,
                candidate.Result.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "unknown year",
                candidateYear ?? "unknown year");
        }

        if (best is null)
        {
            var closest = candidatesForSelection[0];
            _logger.LogInformation(
                "  > No confident metadata match for {Title} ({Year}) - {Count} title match(es) found, none corroborated by duration or year - closest was {ClosestName} ({Similarity:P0} title similarity) - leaving unmatched.",
                candidateTitle,
                candidateYear ?? "unknown year",
                candidatesForSelection.Count,
                closest.Result.Name,
                closest.Similarity);
            return false;
        }

        var refreshOptions = new MetadataRefreshOptions(_directoryService)
        {
            SearchResult = best.Value.Result,
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
                "  > Applied metadata match: {Title} -> {MatchName} ({Year}, via {Provider}, {Similarity:P0} title similarity{LocalizedTitle}) - accepted on {AcceptedVia}.",
                candidateTitle,
                best.Value.Result.Name,
                best.Value.Result.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "unknown year",
                best.Value.Result.SearchProviderName,
                best.Value.Similarity,
                best.Value.MatchedTitle is null ? string.Empty : $", via localized title {best.Value.MatchedTitle}",
                acceptedVia);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("  > Failed to apply metadata match for {Title}: {Error}", candidateTitle, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Scores every pooled candidate against both its primary title and, for any
    /// candidate TMDb is the source of, its full set of TMDb translations
    /// (<see cref="TmdbTranslationsClient"/>) too - taking whichever of the two
    /// scores best. Deliberately NOT filtered down to the item's own resolved
    /// preferred metadata language first: confirmed live that doing so is actively
    /// wrong (for "75 cl Schicksal", a mixed-language foreign-short-film archive's
    /// LIBRARY-wide configured metadata language resolved to English, not German,
    /// so restricting to "the preferred language's translation" picked the English
    /// one - identical to the primary title, same low score - and never even looked
    /// at the German one that actually matched). A file's own title can be in any
    /// language this plugin has no reliable way to predict in advance; the strict
    /// similarity threshold callers apply afterward is what keeps this safe, not a
    /// language filter. Every candidate is scored this way unconditionally, not only
    /// once the primary title alone has already failed to find anything - confirmed
    /// live why that distinction matters too: two entirely different films can share
    /// the exact same generic localized title, one via a well-curated TMDb
    /// translation and the other by pure coincidence on a less-curated provider's
    /// primary title, and only checking translations "as a last resort" would let
    /// the coincidental, unrelated match win by never even looking at the real one.
    /// </summary>
    private async Task<List<(RemoteSearchResult Result, double Similarity, string? MatchedTitle)>> ScoreCandidatesAsync(
        string candidateTitle,
        List<RemoteSearchResult> results,
        string tmdbApiKey,
        CancellationToken cancellationToken)
    {
        var scored = new List<(RemoteSearchResult Result, double Similarity, string? MatchedTitle)>();

        foreach (var candidate in results)
        {
            var primarySimilarity = Levenshtein.TitleSimilarity(candidateTitle, candidate.Name);
            double bestSimilarity = primarySimilarity;
            string? matchedTitle = null;

            if (candidate.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbId) && !string.IsNullOrEmpty(tmdbId))
            {
                var translations = await _tmdbTranslationsClient.GetTranslatedTitlesAsync(tmdbApiKey, tmdbId, cancellationToken).ConfigureAwait(false);
                if (translations.Count == 0)
                {
                    // Logged rather than silently skipped - otherwise a candidate
                    // with genuinely no localized titles at all is indistinguishable
                    // from one this check never got to run for, which made an
                    // earlier version of this bug (a bad auth header) look like
                    // "nothing found" instead of the request outright failing.
                    _logger.LogInformation("  > {MatchName} (id {TmdbId}) has no TMDb translations to check.", candidate.Name, tmdbId);
                }
                else
                {
                    var bestTranslation = translations
                        .Select(t => (Title: t.Title, Similarity: Levenshtein.TitleSimilarity(candidateTitle, t.Title)))
                        .OrderByDescending(t => t.Similarity)
                        .First();

                    if (bestTranslation.Similarity > bestSimilarity)
                    {
                        bestSimilarity = bestTranslation.Similarity;
                        matchedTitle = bestTranslation.Title;
                        _logger.LogInformation(
                            "  > {Title} matched {MatchName} via its localized title \"{TranslatedTitle}\" ({Similarity:P0} similar) - its primary title only scored {PrimarySimilarity:P0}.",
                            candidateTitle,
                            candidate.Name,
                            bestTranslation.Title,
                            bestTranslation.Similarity,
                            primarySimilarity);
                    }
                }
            }

            scored.Add((candidate, bestSimilarity, matchedTitle));
        }

        return scored;
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
            _logger.LogInformation("  > {Name} has no SearchProviderName - can't fetch its runtime for cross-checking.", candidate.Name);
            return null;
        }

        var libraryOptions = _libraryManager.GetLibraryOptions(movie);
        var provider = _providerManager.GetMetadataProviders<Movie>(movie, libraryOptions)
            .OfType<IRemoteMetadataProvider<Movie, MovieInfo>>()
            .FirstOrDefault(p => string.Equals(p.Name, candidate.SearchProviderName, StringComparison.Ordinal));

        if (provider is null)
        {
            _logger.LogInformation("  > No active metadata provider named {Provider} to fetch {Name}'s runtime from - is it still enabled for this library?", candidate.SearchProviderName, candidate.Name);
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
                _logger.LogInformation("  > {Provider} returned no runtime for {Name} - can't cross-check it against the local file.", candidate.SearchProviderName, candidate.Name);
                return null;
            }

            return TimeSpan.FromTicks(result.Item.RunTimeTicks.Value).TotalMinutes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "  > Could not fetch full metadata for candidate {Name} from {Provider}: {Error}",
                candidate.Name,
                candidate.SearchProviderName,
                ex.Message);
            return null;
        }
    }
}
