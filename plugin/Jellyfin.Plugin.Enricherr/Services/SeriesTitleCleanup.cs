using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Strips season-range noise ("S1", "S1 - S5", "Season 1-5", ...) from a TV series'
/// resolved title candidates. Deliberately a small, separate post-processing step
/// applied only in the series path, rather than folded into the shared
/// <see cref="ItemMetadata"/>/<see cref="TitleMatching"/> logic - a movie title never
/// has this kind of noise, so it has no business being in code the movie path also
/// runs. A multi-season "complete series" bundle folder (e.g. "... S1 - S5 [Full
/// Series] ...") can leave a season-range fragment in both Jellyfin's own matched Name
/// and the folder name itself - confirmed live: Jellyfin's Name for one such folder
/// came back as "Food Wars! (Shokugeki no Soma) S1" (only Jellyfin's own matching
/// already dropped the "- S5" part), which made every search query wrong since the
/// real trailers are titled just "Food Wars!"/"Shokugeki no Soma", no season suffix.
/// The two candidates are similar enough to each other that
/// TitleMatching.PreferFilenameOverMetadata's mismatch heuristic (built for a wrong
/// provider match, not consistently-polluted-but-agreeing metadata) doesn't catch it.
/// </summary>
public static partial class SeriesTitleCleanup
{
    [GeneratedRegex(@"\b(?:seasons?\s+\d{1,2}(?:\s*[-–—]\s*\d{1,2})?|s\d{1,2}(?:\s*[-–—]\s*s?\d{1,2})?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRangeRegex();

    /// <summary>
    /// Removes a season-range fragment (if any), along with the parenthetical/bracket
    /// pair it can leave empty behind ("Blue Lock (Season 1)" -&gt; "Blue Lock ( )" if
    /// only the inner text were removed) and any stray dangling separator, then
    /// collapses whitespace.
    /// </summary>
    public static string StripSeasonRange(string title)
    {
        var cleaned = SeasonRangeRegex().Replace(title, " ");
        cleaned = Regex.Replace(cleaned, @"\(\s*\)|\[\s*\]", " ");
        cleaned = Regex.Replace(cleaned, @"\s*-\s*$", string.Empty);
        cleaned = Regex.Replace(cleaned, @"^\s*-\s*", string.Empty);
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }
}
