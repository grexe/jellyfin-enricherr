using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Filesystem-level operations on a TV series' own top-level folder.
/// </summary>
public static class SeriesFileOperations
{
    /// <summary>
    /// Renames a series' own top-level folder to match its resolved title (e.g. a
    /// release-style "[Group] Series Name S1 - S3 [Tags]" folder becomes "Series Name
    /// (Year)"). Only the top-level folder is touched - season subfolders and
    /// everything inside them move along with it unchanged, nothing inside is
    /// renamed or restructured. Unlike a movie's own file rename, a series folder can
    /// still resolve to the right trailer via search even when left messy (Jellyfin's
    /// provider matching is forgiving), but the folder name itself is what drives
    /// Jellyfin's own UI (poster match in the collection/list view, displayed title) -
    /// this exists to fix that, independently of trailer search. Safe to call
    /// repeatedly: a folder that already matches safeTitle is left untouched.
    /// </summary>
    /// <returns>The series' folder path after the call, and whether a rename happened.</returns>
    public static (string NewSeriesPath, bool Renamed) RenameSeriesFolder(string seriesPath, string safeTitle, bool dryRun, string? libraryRoot, ILogger logger)
    {
        var parentPath = Path.GetDirectoryName(seriesPath) ?? string.Empty;
        var currentFolderName = Path.GetFileName(seriesPath);

        if (currentFolderName == safeTitle)
        {
            return (seriesPath, false);
        }

        var targetPath = Path.Combine(parentPath, safeTitle);

        if (dryRun)
        {
            logger.LogInformation("  > [DRY-RUN] Would rename series folder to: {Title}", safeTitle);
            return (seriesPath, true);
        }

        if (Directory.Exists(targetPath))
        {
            logger.LogWarning("  > Target folder {Folder} already exists. Skipping series folder rename.", safeTitle);
            return (seriesPath, false);
        }

        try
        {
            Directory.Move(seriesPath, targetPath);
            logger.LogInformation("  > Series folder renamed to: {Folder}", safeTitle);
            return (targetPath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError("  > Failed to rename series folder {Folder} to {Title}: {Error}", PathDisplay.Relative(seriesPath, libraryRoot), safeTitle, e.Message);
            return (seriesPath, false);
        }
    }

    /// <summary>
    /// The canonical folder name for a season, given Jellyfin's own resolved season
    /// number - "Specials" for 0, "Season NN" (zero-padded) otherwise. Deliberately not
    /// derived from the season folder's own (possibly messy) name - <paramref
    /// name="indexNumber"/> comes from Jellyfin's own Season entity, the same reliable
    /// matching that already finds the right trailer despite messy folder names.
    /// </summary>
    public static string? SeasonFolderName(int? indexNumber)
    {
        return indexNumber switch
        {
            null => null,
            0 => "Specials",
            var n => $"Season {n:D2}"
        };
    }

    /// <summary>
    /// Renames a single season subfolder to its canonical name (see
    /// <see cref="SeasonFolderName"/>). Only the subfolder itself is renamed - its
    /// contents (episode files, extras, ...) move along with it unchanged. Safe to
    /// call repeatedly: a folder that already matches the canonical name is left
    /// untouched; a season with no resolved number (<paramref name="indexNumber"/> is
    /// null) is left untouched too, since there's no safe target name to rename it to.
    /// </summary>
    /// <returns>Whether a rename happened.</returns>
    public static bool RenameSeasonFolder(string seasonPath, int? indexNumber, bool dryRun, string? libraryRoot, ILogger logger)
    {
        var targetName = SeasonFolderName(indexNumber);
        if (targetName is null)
        {
            return false;
        }

        var parentPath = Path.GetDirectoryName(seasonPath) ?? string.Empty;
        var currentFolderName = Path.GetFileName(seasonPath);

        if (currentFolderName == targetName)
        {
            return false;
        }

        var targetPath = Path.Combine(parentPath, targetName);

        if (dryRun)
        {
            logger.LogInformation("  > [DRY-RUN] Would rename season folder to: {Name}", targetName);
            return true;
        }

        if (Directory.Exists(targetPath))
        {
            logger.LogWarning("  > Target folder {Folder} already exists. Skipping season folder rename.", targetName);
            return false;
        }

        try
        {
            Directory.Move(seasonPath, targetPath);
            logger.LogInformation("  > Season folder renamed to: {Name}", targetName);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError("  > Failed to rename season folder {Folder} to {Name}: {Error}", PathDisplay.Relative(seasonPath, libraryRoot), targetName, e.Message);
            return false;
        }
    }
}
