using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Resolves a media item's display title, title variants (for search/matching), and
/// release year from its Jellyfin metadata and its own filename/folder name - ported
/// from the standalone script's resolve_movie_titles/resolve_movie_year. Works on
/// <see cref="BaseItem"/> - Name, OriginalTitle, ProductionYear, and PremiereDate all
/// come from there - so the same logic applies unchanged to a movie (where the local
/// path is its file) and a TV series (where it's the series' own folder); the
/// folder-name-vs-metadata trust heuristic is just as meaningful for a mismatched
/// series folder as for a mismatched movie filename. Unlike the standalone script (an
/// external process talking to the Jellyfin HTTP API over a possibly-translated NAS
/// path), this runs inside the server itself: the local path is simply
/// <c>item.Path</c>, no path mapping needed.
/// </summary>
public static class ItemMetadata
{
    /// <summary>
    /// Determine the item's release year. Prefers the year embedded in the file/folder's
    /// own name over Jellyfin's ProductionYear/PremiereDate when the two disagree - a
    /// movie correctly named "Chang An (2023).mkv" on disk should not get treated as a
    /// 2012 film just because Jellyfin matched it to a same-named 2012 film's metadata.
    /// </summary>
    /// <param name="item">The item to resolve a year for.</param>
    /// <param name="localPath">Path to the item's local file/folder, for its embedded year.</param>
    /// <param name="trustMetadata">
    /// When true, always prefers the metadata year over a disagreeing file-embedded
    /// one, instead of the other way around. Only meant for re-resolving right after
    /// <see cref="MissingMetadataMatcher"/> has just confidently applied a match this
    /// same run - confirmed live that without this, a file embedding an unrelated
    /// archival/broadcast date (not its real release year) always won out over the
    /// freshly-matched, actually-correct year, silently undoing the very match this
    /// plugin just made and feeding a wrong-year trailer search query.
    /// </param>
    public static string? ResolveYear(BaseItem item, string localPath, bool trustMetadata = false)
    {
        var fileStem = localPath.Length > 0 ? Path.GetFileNameWithoutExtension(localPath) : string.Empty;
        var (_, fileYear) = TitleMatching.CleanMediaTitle(fileStem);

        string? metadataYear = item.ProductionYear?.ToString();
        if (string.IsNullOrEmpty(metadataYear) && item.PremiereDate.HasValue)
        {
            metadataYear = item.PremiereDate.Value.Year.ToString();
        }

        if (!trustMetadata && !string.IsNullOrEmpty(fileYear) && !string.IsNullOrEmpty(metadataYear) && fileYear != metadataYear)
        {
            return fileYear;
        }

        if (!string.IsNullOrEmpty(metadataYear))
        {
            return metadataYear;
        }

        if (!string.IsNullOrEmpty(fileYear))
        {
            return fileYear;
        }

        var (_, nameYear) = TitleMatching.CleanMediaTitle(item.Name);
        return nameYear;
    }

    /// <summary>
    /// Determine the preferred title for naming/renaming (honoring Latin locale over
    /// CJK/non-Latin) and collect all title variants for search and trailer filtering.
    /// </summary>
    /// <param name="item">The item to resolve a title for.</param>
    /// <param name="localPath">Path to the item's local file/folder, for its filename-derived title.</param>
    /// <param name="trustMetadata">
    /// When true, skips <see cref="TitleMatching.PreferFilenameOverMetadata"/> entirely
    /// (always trusts <paramref name="item"/>'s own Name/OriginalTitle) AND drops the
    /// file-stem candidate from the returned title variants entirely, rather than
    /// keeping it as a fallback. Only meant for re-resolving right after
    /// <see cref="MissingMetadataMatcher"/> has just confidently applied a match this
    /// same run: the filename-distrust heuristic exists to catch Jellyfin's own
    /// automatic matching going wrong (a new Name sharing little vocabulary with the
    /// filename), but this plugin's own localized-title matching deliberately
    /// produces exactly that shape of result on purpose (a German filename correctly
    /// matched to its French/English TMDb title) - confirmed live, without this the
    /// heuristic silently reverted the title right back to the raw, unmatched
    /// filename immediately after a confident match was just applied. The file stem
    /// is dropped from the variants for the same reason: it's the one candidate this
    /// plugin already positively knows not to trust here (that's the whole reason a
    /// search was needed), so keeping it as a trailer-search fallback only wastes
    /// YouTube requests searching a title already known to be wrong.
    /// </param>
    public static (string PreferredTitle, List<string> TitleVariants) ResolveTitles(BaseItem item, string localPath, bool trustMetadata = false)
    {
        var rawName = string.IsNullOrEmpty(item.Name) ? "Unknown" : item.Name;
        var originalTitle = item.OriginalTitle ?? string.Empty;
        var fileStem = localPath.Length > 0 ? Path.GetFileNameWithoutExtension(localPath) : string.Empty;

        var (cleanedName, _) = TitleMatching.CleanMediaTitle(rawName);
        var (cleanedOrig, _) = TitleMatching.CleanMediaTitle(originalTitle);
        var (cleanedStem, _) = TitleMatching.CleanMediaTitle(fileStem);

        var nameCand = cleanedName.Length > 0 ? cleanedName : rawName;
        var origCand = cleanedOrig.Length > 0 ? cleanedOrig : originalTitle;
        var stemCand = cleanedStem.Length > 0 ? cleanedStem : fileStem;

        // Jellyfin's "Name" metadata can be wrong in a way that no amount of noise-
        // stripping fixes (a bad provider match) - trust the filename instead when it
        // looks untrustworthy. See TitleMatching.PreferFilenameOverMetadata.
        var distrustMetadata = !trustMetadata && TitleMatching.PreferFilenameOverMetadata(nameCand, stemCand);
        if (distrustMetadata)
        {
            nameCand = stemCand;
        }

        string preferredTitle;
        if (!TitleMatching.IsNonLatin(nameCand))
        {
            preferredTitle = nameCand;
        }
        else if (!string.IsNullOrEmpty(origCand) && !TitleMatching.IsNonLatin(origCand))
        {
            preferredTitle = origCand;
        }
        else if (!string.IsNullOrEmpty(stemCand) && !TitleMatching.IsNonLatin(stemCand))
        {
            preferredTitle = stemCand;
        }
        else
        {
            preferredTitle = nameCand;
        }

        // trustMetadata drops the file-stem candidate entirely: it's only ever
        // included as a fallback for the case where Jellyfin's own metadata might
        // itself be untrustworthy - but trustMetadata means this plugin just
        // independently verified the metadata itself (MissingMetadataMatcher), so
        // the file stem is the one candidate we already positively know NOT to
        // trust here (that mismatch is the entire reason a search was needed at
        // all). Confirmed live: without this, a trailer search kept wastefully
        // retrying the raw archive-numbered filename ("Der Briefwechsel-101840-000-A")
        // as a final fallback stage even after successfully matching and renaming to
        // the real title - guaranteed to find nothing beyond what the real title's
        // own searches already tried, just burning extra YouTube requests and
        // rate-limit exposure for certain failure.
        List<string> orderedCandidates;
        if (distrustMetadata)
        {
            // A wrong Name usually means Jellyfin matched this item to an entirely
            // different item, so OriginalTitle and the raw Name are from that same
            // wrong match too and can't be trusted either as fallback candidates.
            orderedCandidates = [preferredTitle];
        }
        else if (!TitleMatching.IsNonLatin(preferredTitle))
        {
            orderedCandidates = trustMetadata ? [preferredTitle, origCand, rawName] : [preferredTitle, origCand, stemCand, rawName];
        }
        else
        {
            orderedCandidates = trustMetadata ? [rawName, origCand, preferredTitle] : [rawName, origCand, stemCand, preferredTitle];
        }

        var titleVariants = new List<string>();
        foreach (var t in orderedCandidates)
        {
            if (!string.IsNullOrEmpty(t) && !titleVariants.Contains(t, StringComparer.Ordinal))
            {
                titleVariants.Add(t);
            }
        }

        return (preferredTitle, titleVariants);
    }
}
