using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Maps a metadata language code (as returned by <c>BaseItem.GetPreferredMetadataLanguage()</c>,
/// which resolves through the item's library/server configuration - no interactive
/// Jellyfin user session needed) to that language's own word(s) for "trailer" - used by
/// <see cref="TrailerSources"/> to bias the YouTube search toward a trailer actually
/// uploaded/titled in the item's own preferred language, rather than always searching
/// in English regardless of the library's configured language. Generalizes what was
/// already a Japanese-only special case (script-detection triggering "予告"/"PV"
/// queries) into a broader, explicit per-language table.
///
/// Best-effort and non-exhaustive: a language missing here just means no native-language
/// query stage is tried for it - English (always tried regardless, see TrailerSources)
/// and the bare-title fallback still apply, so this never turns into "no trailer found"
/// on its own. If a common language is missing, https://github.com/grexe/jellyfin-enricherr/issues
/// welcomes a report.
/// </summary>
public static class TrailerLanguages
{
    private static readonly Dictionary<string, string[]> NativeTrailerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = ["予告", "PV"],
        ["ko"] = ["예고편"],
        // The language code alone doesn't distinguish Simplified from Traditional
        // Chinese - trying both terms costs nothing extra (an unhelpful search stage
        // just returns no acceptable candidate, same as any other search stage that
        // doesn't pan out).
        ["zh"] = ["预告片", "預告片"],
        ["de"] = ["trailer", "vorschau"],
        ["fr"] = ["bande-annonce"],
        ["es"] = ["tráiler"],
        ["it"] = ["trailer"],
        ["pt"] = ["trailer"],
        ["nl"] = ["trailer"],
        ["ru"] = ["трейлер"],
        ["pl"] = ["zwiastun"],
        ["sv"] = ["trailer"],
        ["da"] = ["trailer"],
        ["no"] = ["trailer"],
        // Jellyfin (and BCP 47 generally) can report Norwegian Bokmål as "nb" instead
        // of the more general "no" depending on configuration.
        ["nb"] = ["trailer"],
        ["fi"] = ["traileri"]
    };

    /// <summary>
    /// Every native word used across every language, flattened - fed into
    /// <see cref="TitleMatching.HasTrailerKeyword"/> so a candidate found via a
    /// native-language search stage isn't then rejected by the (otherwise English/
    /// Japanese-centric) trailer-keyword filter for using its own language's word.
    /// </summary>
    public static IReadOnlyList<string> AllNativeWords { get; } = BuildAllNativeWords();

    /// <summary>
    /// The native trailer word(s) for <paramref name="languageCode"/>, most specific
    /// first, or empty if the language isn't in the table (or wasn't resolvable at
    /// all - not every item necessarily has one).
    /// </summary>
    public static IReadOnlyList<string> GetNativeTrailerWords(string? languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            return Array.Empty<string>();
        }

        // GetPreferredMetadataLanguage() can return a full tag like "de-DE" depending
        // on configuration - only the primary language subtag matters for this lookup.
        var primary = languageCode.Split('-', 2)[0];
        return NativeTrailerWords.TryGetValue(primary, out var words) ? words : Array.Empty<string>();
    }

    private static string[] BuildAllNativeWords()
    {
        var all = new List<string>();
        foreach (var words in NativeTrailerWords.Values)
        {
            all.AddRange(words);
        }

        return all.ToArray();
    }
}
