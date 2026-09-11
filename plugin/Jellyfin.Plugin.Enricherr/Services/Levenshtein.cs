using System;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Plain Levenshtein (edit-distance) string similarity - used to score a remote
/// metadata search candidate's title against our own resolved title
/// (<see cref="MissingMetadataMatcher"/>) strictly enough that an unrelated
/// same-ish-sounding result doesn't get auto-applied to an unattended library item.
/// </summary>
public static class Levenshtein
{
    /// <summary>
    /// Normalized similarity in [0, 1] between two strings - 1 for an exact
    /// (case-insensitive) match, 0 for completely dissimilar. Comparison is
    /// case-insensitive since a search candidate's casing (e.g. title case) and a
    /// locally-derived title's casing routinely differ without that meaning anything
    /// about how well they actually match.
    /// </summary>
    public static double NormalizedSimilarity(string? a, string? b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        if (a.Length == 0 && b.Length == 0)
        {
            return 1.0;
        }

        var distance = Distance(a.ToLowerInvariant(), b.ToLowerInvariant());
        var maxLength = Math.Max(a.Length, b.Length);
        return maxLength == 0 ? 1.0 : 1.0 - ((double)distance / maxLength);
    }

    /// <summary>
    /// A title-aware variant of <see cref="NormalizedSimilarity"/> for matching a
    /// remote search candidate's clean title against our own resolved (possibly still
    /// noisy) one. Plain edit-distance alone badly under-scores exactly the case this
    /// exists for: our own title carrying a trailing archive/catalog number or scene
    /// tag the real title doesn't have (confirmed live: "29, bald 45-039221-004-A" vs.
    /// the real title "29, bald" scores only ~0.33 on raw edit distance, since roughly
    /// half the longer string's length is pure suffix noise) - or a candidate with a
    /// trailing subtitle ("Blue Lock" vs. "Blue Lock: The Movie"). When the shorter of
    /// the two titles is a genuine prefix of the longer one - ending at a real word
    /// boundary, not mid-word ("Cars" is not a match for "Carson") - that counts as a
    /// full match; otherwise falls back to plain edit-distance similarity. This is
    /// only the title signal - callers still separately require a year and runtime
    /// match before treating any of this as confident enough to act on.
    /// </summary>
    public static double TitleSimilarity(string? a, string? b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        var normA = a.Trim().ToLowerInvariant();
        var normB = b.Trim().ToLowerInvariant();

        if (normA.Length > 0 && normB.Length > 0)
        {
            var (shorter, longer) = normA.Length <= normB.Length ? (normA, normB) : (normB, normA);
            if (shorter.Length >= 3 && longer.StartsWith(shorter, StringComparison.Ordinal) &&
                (longer.Length == shorter.Length || !char.IsLetterOrDigit(longer[shorter.Length])))
            {
                return 1.0;
            }
        }

        return NormalizedSimilarity(a, b);
    }

    /// <summary>The classic edit distance - insertions, deletions, and substitutions to turn <paramref name="a"/> into <paramref name="b"/>.</summary>
    private static int Distance(string a, string b)
    {
        var previousRow = new int[b.Length + 1];
        var currentRow = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previousRow[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            currentRow[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                currentRow[j] = Math.Min(
                    Math.Min(currentRow[j - 1] + 1, previousRow[j] + 1),
                    previousRow[j - 1] + cost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[b.Length];
    }
}
