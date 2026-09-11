using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Pure text-processing heuristics for cleaning up scene-release-style filenames/titles
/// and matching them against YouTube search result titles. Ported from the standalone
/// script's title-matching logic (clean_media_title, extract_main_title,
/// create_trailer_filter's title-matching stage, etc.) - see the standalone script's
/// history for how each heuristic came to exist.
/// </summary>
public static partial class TitleMatching
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "der", "die", "das", "le", "la", "les", "el", "los", "il", "lo",
        "and", "of", "in", "on", "at", "to", "for", "with"
    };

    [GeneratedRegex(@"[぀-ヿ㐀-䶿一-鿿豈-﫿ｦ-ﾟ가-힯Ѐ-ӿ؀-ۿ]")]
    private static partial Regex NonLatinRegex();

    /// <summary>Whether the text contains non-Latin scripts (e.g. CJK, Cyrillic, Arabic).</summary>
    public static bool IsNonLatin(string? text)
    {
        return !string.IsNullOrEmpty(text) && NonLatinRegex().IsMatch(text);
    }

    /// <summary>Remove illegal filesystem characters from a filename.</summary>
    public static string SanitizeFilename(string? name)
    {
        var clean = Regex.Replace(name ?? string.Empty, "[\\\\/*?:\"<>|]", string.Empty).Trim();
        return clean.Length > 0 ? clean : "Unknown_Movie";
    }

    [GeneratedRegex(@"\[.*?\]|\{.*?\}")]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"[\(\[]\s*(19\d{2}|20\d{2})\s*[\)\]]")]
    private static partial Regex ParenYearRegex();

    // A year *range* ("(1994-2019)", an anthology/box-set's airing span) doesn't match
    // ParenYearRegex at all (that requires a single year alone in the parens/brackets) -
    // checked first so the whole range gets consumed as one unit. Otherwise
    // StandaloneYearRegex still matches just the first year on its own (its bounding
    // character class treats the dash as a valid delimiter), stripping only "1994" and
    // leaving "( -2019)" behind - confirmed live on a Shinichiro Watanabe anthology
    // folder titled "... (1994-2019) - 8 Complete TV Series, ...".
    [GeneratedRegex(@"[\(\[]\s*(19\d{2}|20\d{2})\s*[-–—]\s*(?:19\d{2}|20\d{2})\s*[\)\]]")]
    private static partial Regex ParenYearRangeRegex();

    [GeneratedRegex(@"(?:^|[\s._\-(])(19\d{2}|20\d{2})(?:$|[\s._\-)])")]
    private static partial Regex StandaloneYearRegex();

    [GeneratedRegex(@"^\s*0*\d{1,3}\s*[\.\-_]\s*")]
    private static partial Regex LeadingTrackNumberPunctRegex();

    private static readonly string[] NoisePatterns =
    [
        @"\b(720p|1080p|1080i|2160p|4k|uhd|hd|sd|480p|360p)\b",
        @"\b(h264|h265|x264|x265|hevc|av1|xvid|divx|10bit|8bit)\b",
        @"\b(aac|e?ac3|e-ac-3|dts|dts-hd|dts-x|dtsx|truehd|atmos|flac|mp3|pcm|opus|ddp|dd5\.1|5\.1)\b",
        @"\b(bluray|blu-ray|bdrip|brrip|web-dl|webrip|web|dvdrip|dvd|hdtv|remux)\b",
        @"\b(eng|jpn|ger|fra|spa|ita|multi|subs?|dub|dual audio|subbed|dubbed)\b",
        @"\b(chinese|mandarin|cantonese|korean|japanese|english|german|french|spanish|" +
        @"italian|russian|thai|vietnamese|hindi|arabic|portuguese|dutch|polish|turkish|" +
        @"swedish|norwegian|danish|finnish|greek|hebrew|indonesian)\b",
        @"\b(animation|anime)\b",
        @"\b(mp4|mkv|avi|vob|iso)\b"
    ];

    private static readonly Regex[] NoiseRegexes = NoisePatterns
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .ToArray();

    /// <summary>
    /// Clean raw media title/filename of scene release tags, track numbers, codecs, and
    /// brackets, and extract the year if present.
    /// </summary>
    public static (string Title, string? Year) CleanMediaTitle(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return (string.Empty, null);
        }

        var s = BracketedRegex().Replace(title, " ");

        string? year = null;
        var rangeMatch = ParenYearRangeRegex().Match(title);
        var parenMatch = ParenYearRegex().Match(title);
        if (rangeMatch.Success)
        {
            year = rangeMatch.Groups[1].Value;
            s = ParenYearRangeRegex().Replace(s, " ");
        }
        else if (parenMatch.Success)
        {
            year = parenMatch.Groups[1].Value;
            s = Regex.Replace(s, @"[\(\[]\s*" + Regex.Escape(year) + @"\s*[\)\]]", " ");
        }
        else
        {
            var standMatch = StandaloneYearRegex().Match(s);
            if (standMatch.Success)
            {
                var g = standMatch.Groups[1];
                year = g.Value;
                s = s[..g.Index] + " " + s[(g.Index + g.Length)..];
            }
        }

        // Only a punctuation-anchored prefix ("01. ", "01 - ", "01_") is treated as a
        // scene-release/track-number artifact to strip. A bare "number + space" prefix
        // (no separator) was also stripped here previously, but that has no structural
        // signal distinguishing a real track number from a title that genuinely starts
        // with a digit ("5 Centimeters per Second", "12 Monkeys", "300", "8 Mile") -
        // confirmed live: it silently turned "5 Centimeters Per Second" into "Centimeters
        // Per Second" and broke every search query for that title.
        s = LeadingTrackNumberPunctRegex().Replace(s, string.Empty);

        foreach (var re in NoiseRegexes)
        {
            s = re.Replace(s, " ");
        }

        s = s.Replace('_', ' ').Replace('.', ' ');
        s = Regex.Replace(s, @"\(\s*\)|\[\s*\]", " ");
        s = Regex.Replace(s, @"\s*-\s*$", string.Empty);
        s = Regex.Replace(s, @"^\s*-\s*", string.Empty);
        s = Regex.Replace(s, @"\s+", " ").Trim();

        return (s, year);
    }

    [GeneratedRegex(@"\s+[-–—:|/]\s+|\s*[:/]\s*")]
    private static partial Regex SubtitleDelimiterRegex();

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex NonWordRegex();

    /// <summary>
    /// Extract the clean title without year, the primary main title (before subtitle
    /// separators), and the primary first word, for fuzzy matching.
    /// </summary>
    public static (string Clean, string Main, string FirstWord) ExtractMainTitle(string? title)
    {
        var (cleanT, _) = CleanMediaTitle(title);
        var clean = cleanT.Length > 0 ? cleanT : (title ?? string.Empty);

        var parts = SubtitleDelimiterRegex().Split(clean);
        var main = parts.Length > 0 ? parts[0].Trim() : clean;

        var cleanWords = clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var rawFirst = cleanWords.Length > 0 ? cleanWords[0].Trim() : clean;
        var firstWord = NonWordRegex().Replace(rawFirst, string.Empty).Trim();

        return (clean, main, firstWord);
    }

    /// <summary>Lowercased, punctuation-stripped significant words (filler words excluded).</summary>
    public static HashSet<string> SignificantWords(string text)
    {
        var norm = Regex.Replace(NonWordRegex().Replace(text.ToLowerInvariant(), " "), @"\s+", " ").Trim();
        return norm.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a movie's filename-derived title should be trusted over Jellyfin's
    /// "Name"-derived metadata: both are Latin-script, differ, the filename is substantial
    /// (not just "movie"), and less than half of the metadata's own significant words are
    /// shared with it.
    /// </summary>
    public static bool PreferFilenameOverMetadata(string? nameCand, string? stemCand)
    {
        if (string.IsNullOrEmpty(stemCand) || IsNonLatin(stemCand) ||
            string.IsNullOrEmpty(nameCand) || IsNonLatin(nameCand) ||
            stemCand == nameCand)
        {
            return false;
        }

        if (stemCand.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 2)
        {
            return false;
        }

        var nameWords = SignificantWords(nameCand);
        if (nameWords.Count == 0)
        {
            return false;
        }

        var stemWords = SignificantWords(stemCand);
        var overlap = stemWords.Count(nameWords.Contains);
        return (double)overlap / nameWords.Count < 0.5;
    }

    [GeneratedRegex(@"^(19\d{2}|20\d{2})\b")]
    private static partial Regex YearSuffixRegex();

    [GeneratedRegex(@"^(\d{1,2}|ii|iii|iv|v|vi|vii|viii|ix|x|part\s*\d{1,2}|chapter\s*\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SequelSuffixRegex();

    /// <summary>Whether the text right after a matched title candidate still counts as a match.</summary>
    public static bool TitleSuffixAllowed(string remainder)
    {
        if (string.IsNullOrEmpty(remainder) || YearSuffixRegex().IsMatch(remainder))
        {
            return true;
        }

        return !SequelSuffixRegex().IsMatch(remainder);
    }

    /// <summary>
    /// Whether the text right before a matched title candidate still counts as a match:
    /// the title's own words must be the dominant part of the string, not buried after
    /// substantial unrelated text.
    /// </summary>
    public static bool TitlePrefixAllowed(string prefix, int matchWordCount)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return true;
        }

        return prefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= matchWordCount;
    }

    /// <summary>
    /// Whether a YouTube result title matches one of the movie's title variants, using
    /// the same phrase/word-overlap heuristics as the standalone script's match_filter.
    /// </summary>
    public static bool TitleMatches(string ytTitle, IEnumerable<string> titleVariants)
    {
        // Both sides of the phrase match below must be lowercase - normCand already is
        // (derived from candLower), but ytTitle itself is whatever case YouTube gave it
        // (almost always title-case), and Regex.Match is case-sensitive by default. The
        // standalone script avoids this by lowercasing yt_title once at the very top of
        // its filter function, before anything derived from it gets used.
        var normYt = Regex.Replace(NonWordRegex().Replace(ytTitle.ToLowerInvariant(), " "), @"\s+", " ").Trim();

        foreach (var tv in titleVariants)
        {
            var (cleanT, mainT, firstW) = ExtractMainTitle(tv);

            List<string> candidates;
            if (IsNonLatin(cleanT) || mainT == firstW)
            {
                candidates = [cleanT, mainT, firstW];
            }
            else
            {
                candidates = [cleanT, mainT];
            }

            foreach (var cand in candidates)
            {
                var candLower = cand.ToLowerInvariant().Trim();
                if (candLower.Length == 0)
                {
                    continue;
                }

                if (IsNonLatin(candLower))
                {
                    if (ytTitle.ToLowerInvariant().Contains(candLower, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    continue;
                }

                var normCand = Regex.Replace(NonWordRegex().Replace(candLower, " "), @"\s+", " ").Trim();
                if (normCand.Length == 0)
                {
                    continue;
                }

                var phraseMatch = Regex.Match(normYt, @"\b" + Regex.Escape(normCand) + @"\b");
                if (phraseMatch.Success)
                {
                    var prefixOk = TitlePrefixAllowed(normYt[..phraseMatch.Index].Trim(), normCand.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
                    var suffixOk = TitleSuffixAllowed(normYt[(phraseMatch.Index + phraseMatch.Length)..].Trim());
                    if (prefixOk && suffixOk)
                    {
                        return true;
                    }

                    continue;
                }

                var words = normCand.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 1 && !StopWords.Contains(w))
                    .ToList();
                if (words.Count >= 2 && words.All(w => Regex.IsMatch(normYt, @"\b" + Regex.Escape(w) + @"\b")))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Plus every language's own native trailer word (see TrailerLanguages) - a
    // candidate found via a native-language search stage shouldn't then get rejected
    // here for using its own language's word instead of an English/Japanese one.
    private static readonly string[] TrailerKeywords =
    [
        "trailer", "teaser", "vorschau", "preview", "clip", "pv", "予告", "特報", "本予告", "cm", "sub",
        .. TrailerLanguages.AllNativeWords
    ];

    /// <summary>Whether a YouTube result title contains a trailer-ish keyword.</summary>
    public static bool HasTrailerKeyword(string ytTitle)
    {
        var lower = ytTitle.ToLowerInvariant();
        return TrailerKeywords.Any(kw => lower.Contains(kw.ToLowerInvariant(), StringComparison.Ordinal));
    }
}
