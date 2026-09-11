using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Builds the ordered list of sources to try for a media item's trailer: official
/// RemoteTrailers first, then a multi-stage set of YouTube searches - native-language
/// queries for the item's own preferred metadata language (<see cref="TrailerLanguages"/>)
/// and/or a non-Latin title, then English (full title + year, main title + year, broad
/// query). Ported from the standalone script's get_trailer_sources. Works on
/// <see cref="BaseItem"/> - RemoteTrailers is declared there - so the same
/// query-building logic applies unchanged to a movie or a TV series.
/// </summary>
public static class TrailerSources
{
    /// <summary>
    /// Build the ordered list of sources: direct YouTube URLs first, then
    /// "ytsearch5:&lt;query&gt;" strings.
    /// </summary>
    /// <param name="item">The movie/series to build sources for.</param>
    /// <param name="titleVariants">Resolved title variants to search with.</param>
    /// <param name="year">The item's release year, if known.</param>
    /// <param name="skipNativeLanguage">
    /// Skips the native-language (and non-Latin-title) query stages entirely, going
    /// straight to English/bare-title queries - used for an <em>upgrade</em> re-search
    /// specifically (see <see cref="Configuration.PluginConfiguration.AllowUpgradeInOtherLanguage"/>),
    /// where the search loop otherwise stops at the first successful download
    /// regardless of resolution: a native-language stage succeeding first, even at
    /// low quality, would mean the English stage - which often has a genuinely
    /// higher-quality upload available - never even gets tried.
    /// </param>
    public static List<string> Build(BaseItem item, List<string> titleVariants, string? year, bool skipNativeLanguage = false)
    {
        var sourcesToTry = new List<string>();

        foreach (var rt in item.RemoteTrailers)
        {
            var url = rt.Url;
            if (!string.IsNullOrEmpty(url) &&
                (url.Contains("youtube", System.StringComparison.OrdinalIgnoreCase) || url.Contains("youtu.be", System.StringComparison.OrdinalIgnoreCase)) &&
                !sourcesToTry.Contains(url))
            {
                sourcesToTry.Add(url);
            }
        }

        // Biases the search toward a trailer actually uploaded/titled in the item's
        // own preferred metadata language (resolved per-item via its library/server
        // configuration - GetPreferredMetadataLanguage needs no interactive Jellyfin
        // user session) before falling back to English, rather than always searching
        // in English regardless of how the library is actually configured. Never a
        // hard restriction: English and the bare-title fallback below are still
        // always tried afterward too, so a missing/unhelpful native-language stage
        // never turns "no trailer found" on its own.
        var nativeWords = skipNativeLanguage ? Array.Empty<string>() : TrailerLanguages.GetNativeTrailerWords(item.GetPreferredMetadataLanguage());

        var queries = new List<string>();
        foreach (var cand in titleVariants)
        {
            var (cleanCand, mainCand, _) = TitleMatching.ExtractMainTitle(cand);
            var nonLatin = !skipNativeLanguage && TitleMatching.IsNonLatin(cleanCand);

            foreach (var word in nativeWords)
            {
                if (year is not null)
                {
                    queries.Add($"{cleanCand} {year} {word}");
                    if (mainCand != cleanCand)
                    {
                        queries.Add($"{mainCand} {year} {word}");
                    }
                }

                queries.Add($"{cleanCand} {word}");
                if (mainCand != cleanCand)
                {
                    queries.Add($"{mainCand} {word}");
                }
            }

            if (nonLatin)
            {
                // A fallback for a non-Latin *title* even when the preferred metadata
                // language isn't Japanese (e.g. unset, or a different non-Latin
                // language without its own table entry yet) - already covered by
                // nativeWords above whenever the preferred language is "ja" itself.
                // No "official"-style qualifier: that doesn't translate the same way,
                // and Japanese/Chinese/Korean trailer uploads aren't conventionally
                // titled that way to begin with.
                if (year is not null)
                {
                    queries.Add($"{cleanCand} {year} 予告");
                }

                queries.Add($"{cleanCand} 予告");
                queries.Add($"{cleanCand} PV");
                if (mainCand != cleanCand)
                {
                    queries.Add($"{mainCand} 予告");
                    queries.Add($"{mainCand} PV");
                }
            }

            if (year is not null)
            {
                queries.Add($"{cleanCand} {year} official trailer");
                if (mainCand != cleanCand)
                {
                    queries.Add($"{mainCand} {year} official trailer");
                }
            }

            queries.Add($"{cleanCand} official trailer");
            queries.Add($"{cleanCand} trailer");
            if (mainCand != cleanCand)
            {
                queries.Add($"{mainCand} official trailer");
                queries.Add($"{mainCand} trailer");
            }

            if (year is not null)
            {
                queries.Add($"{cleanCand} {year}");
                if (mainCand != cleanCand)
                {
                    queries.Add($"{mainCand} {year}");
                }
            }

            if (mainCand != cleanCand)
            {
                queries.Add(mainCand);
            }
        }

        foreach (var q in queries)
        {
            var qStrip = q.Trim();
            if (qStrip.Length == 0)
            {
                continue;
            }

            var source = $"ytsearch5:{qStrip}";
            if (!sourcesToTry.Contains(source))
            {
                sourcesToTry.Add(source);
            }
        }

        return sourcesToTry;
    }
}
