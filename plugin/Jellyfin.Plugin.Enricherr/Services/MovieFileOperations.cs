using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Filesystem-level operations on a movie's own file(s): validating it's a real media
/// file, renaming it to a resolved title, and migrating it (with its sidecar files) into
/// its own dedicated folder. Ported from the standalone script's is_valid_media_file,
/// rename_movie_file, and migrate_movie_to_own_folder.
/// </summary>
public static class MovieFileOperations
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts", ".iso", ".vob",
        // Confirmed live: an older DVD-era rip (.mpg) was skipped outright ("Not a video
        // file") purely because it was missing here, never getting a trailer at all.
        // Rounded out with the rest of the legitimate real-world video containers this
        // list was already missing, not just the one that happened to be reported.
        ".mpg", ".mpeg", ".flv", ".3gp", ".3g2", ".divx", ".asf", ".rm", ".rmvb", ".mts", ".m2v", ".ogv"
    };

    private static readonly HashSet<string> IgnoredDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "behind the scenes", "deleted scenes", "featurettes", "interviews", "scenes", "shorts", "trailers"
    };

    private const long MinMediaSizeBytes = 1024 * 1024; // 1 MB minimum for a valid movie video file

    /// <summary>
    /// Whether a movie already lives in a folder named after its own file (the same
    /// check <see cref="MigrateToOwnFolder"/> itself uses to decide there's nothing
    /// to do) - shared with theme-song fetching (which requires this, to avoid
    /// misattributing a shared folder's theme.mp3 across every movie in it) and the
    /// settings page's library-totals count, so both agree with migration's own idea
    /// of "already in its own folder".
    /// </summary>
    public static bool HasOwnFolder(string moviePath)
    {
        var folder = Path.GetDirectoryName(moviePath);
        return !string.IsNullOrEmpty(folder) &&
               string.Equals(Path.GetFileName(folder), Path.GetFileNameWithoutExtension(moviePath), StringComparison.Ordinal);
    }

    /// <summary>Whether the local path is a valid main movie video file (not a trailer, sample, or extra).</summary>
    public static bool IsValidMediaFile(string localPath, out string? reason)
    {
        if (!File.Exists(localPath))
        {
            reason = "File does not exist";
            return false;
        }

        var ext = Path.GetExtension(localPath);
        if (!VideoExtensions.Contains(ext))
        {
            reason = $"Not a video file (extension: {ext})";
            return false;
        }

        long fileSize;
        try
        {
            fileSize = new FileInfo(localPath).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            reason = $"Could not determine file size: {e.Message}";
            return false;
        }

        if (fileSize < MinMediaSizeBytes)
        {
            reason = $"File size too small ({fileSize} bytes < {MinMediaSizeBytes} bytes)";
            return false;
        }

        var filenameLower = Path.GetFileName(localPath).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(filenameLower);

        if (stem == "trailer" || stem.EndsWith("-trailer", StringComparison.Ordinal) ||
            stem.EndsWith("_trailer", StringComparison.Ordinal) || stem.EndsWith(".trailer", StringComparison.Ordinal))
        {
            reason = "File is already a trailer";
            return false;
        }

        if (stem == "sample" || stem.EndsWith("-sample", StringComparison.Ordinal) ||
            stem.EndsWith("_sample", StringComparison.Ordinal) || stem.EndsWith(".sample", StringComparison.Ordinal) ||
            filenameLower.Contains(".sample.", StringComparison.Ordinal))
        {
            reason = "File is a sample clip";
            return false;
        }

        var pathParts = Path.GetFullPath(localPath).Split(Path.DirectorySeparatorChar);
        if (pathParts.Take(pathParts.Length - 1).Any(p => IgnoredDirNames.Contains(p)))
        {
            reason = "File is located inside an extras directory";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Rename the movie file to "&lt;safeTitle&gt;&lt;ext&gt;". If the movie already lives in its
    /// own dedicated folder (the folder's name matches the file's current stem), the
    /// folder is renamed to match too - otherwise a later migrate call would see a
    /// mismatch and nest a new, wrongly-named folder inside the existing one.
    /// </summary>
    public static (string NewLocalPath, bool Renamed) RenameMovieFile(string localPath, string safeTitle, bool dryRun, string? libraryRoot, ILogger logger)
    {
        var folderPath = Path.GetDirectoryName(localPath) ?? string.Empty;
        var ext = Path.GetExtension(localPath);
        var currentStem = Path.GetFileNameWithoutExtension(localPath);

        var folderIsDedicated = Path.GetFileName(folderPath) == currentStem;
        var targetFolder = folderPath;
        if (folderIsDedicated && Path.GetFileName(folderPath) != safeTitle)
        {
            targetFolder = Path.Combine(Path.GetDirectoryName(folderPath) ?? string.Empty, safeTitle);
        }

        var targetMoviePath = Path.Combine(targetFolder, $"{safeTitle}{ext}");
        if (targetMoviePath == localPath)
        {
            return (localPath, false);
        }

        if (dryRun)
        {
            if (targetFolder != folderPath)
            {
                logger.LogInformation("  > [DRY-RUN] Would rename folder + file to: {Title}/{Title}{Ext}", safeTitle, safeTitle, ext);
            }
            else
            {
                logger.LogInformation("  > [DRY-RUN] Would rename original file to: {Name}", Path.GetFileName(targetMoviePath));
            }

            return (localPath, true);
        }

        if (targetFolder != folderPath)
        {
            if (Directory.Exists(targetFolder))
            {
                logger.LogWarning("  > Target folder {Folder} already exists. Skipping folder rename.", Path.GetFileName(targetFolder));
                targetFolder = folderPath;
                targetMoviePath = Path.Combine(targetFolder, $"{safeTitle}{ext}");
            }
            else
            {
                try
                {
                    Directory.Move(folderPath, targetFolder);
                    logger.LogInformation("  > Folder renamed to: {Folder}", Path.GetFileName(targetFolder));
                    localPath = Path.Combine(targetFolder, Path.GetFileName(localPath));
                    folderPath = targetFolder;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    logger.LogError("  > Failed to rename folder {Folder}: {Error}", PathDisplay.Relative(folderPath, libraryRoot), e.Message);
                    targetMoviePath = Path.Combine(folderPath, $"{safeTitle}{ext}");
                }
            }
        }

        if (localPath == targetMoviePath)
        {
            return (localPath, true);
        }

        if (File.Exists(targetMoviePath))
        {
            logger.LogWarning("  > Target file {Name} already exists. Skipping rename.", Path.GetFileName(targetMoviePath));
            return (localPath, false);
        }

        try
        {
            File.Move(localPath, targetMoviePath);
            logger.LogInformation("  > Original file renamed to: {Name}", Path.GetFileName(targetMoviePath));
            return (targetMoviePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError("  > Failed to rename file for {Title}: {Error}", safeTitle, e.Message);
            return (localPath, false);
        }
    }

    /// <summary>
    /// Whether a filename (without extension) belongs to the given movie: an exact
    /// match, or a suffix attached with '.', '-' or '_' (subtitles, artwork, or an
    /// already-downloaded trailer).
    /// </summary>
    private static bool IsSidecarOf(string entryStem, string movieStem)
    {
        return entryStem == movieStem ||
               entryStem.StartsWith(movieStem + ".", StringComparison.Ordinal) ||
               entryStem.StartsWith(movieStem + "-", StringComparison.Ordinal) ||
               entryStem.StartsWith(movieStem + "_", StringComparison.Ordinal);
    }

    /// <summary>
    /// Move a movie file - and any sidecar files that belong to it - into a dedicated
    /// subfolder named after the movie file itself. Jellyfin's local-extras resolver
    /// only recognizes a local trailer when the movie has its own folder
    /// (https://github.com/jellyfin/jellyfin/issues/10077). <paramref name="extraStems"/>
    /// should include the title-based stem used for the trailer filename in case it
    /// differs from the movie file's own (possibly still-messy) name. Safe to call
    /// repeatedly: a movie already living in its own folder is left untouched.
    /// </summary>
    public static (string NewLocalPath, bool Moved) MigrateToOwnFolder(string localPath, bool dryRun, IEnumerable<string> extraStems, string? libraryRoot, ILogger logger, bool fixPermissions)
    {
        var currentDir = Path.GetDirectoryName(localPath) ?? string.Empty;
        var movieStem = Path.GetFileNameWithoutExtension(localPath);

        if (Path.GetFileName(currentDir) == movieStem)
        {
            return (localPath, false);
        }

        var targetDir = Path.Combine(currentDir, movieStem);
        var stemsToMatch = new HashSet<string>(StringComparer.Ordinal) { movieStem };
        foreach (var s in extraStems)
        {
            if (!string.IsNullOrEmpty(s))
            {
                stemsToMatch.Add(s);
            }
        }

        string[] siblingNames;
        try
        {
            siblingNames = Directory.GetFiles(currentDir).Select(Path.GetFileName).ToArray()!;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("  > Could not list {Dir} for migration: {Error}", PathDisplay.Relative(currentDir, libraryRoot), e.Message);
            return (localPath, false);
        }

        var filesToMove = siblingNames
            .Where(name => stemsToMatch.Any(stem => IsSidecarOf(Path.GetFileNameWithoutExtension(name), stem)))
            .ToList();

        if (filesToMove.Count == 0)
        {
            return (localPath, false);
        }

        if (dryRun)
        {
            logger.LogInformation("  > [DRY-RUN] Would move into own folder {Stem}/: {Files}", movieStem, string.Join(", ", filesToMove));
            return (localPath, true);
        }

        try
        {
            Directory.CreateDirectory(targetDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError("  > Failed to create folder {Dir}: {Error}", PathDisplay.Relative(targetDir, libraryRoot), e.Message);
            return (localPath, false);
        }

        // Confirmed live: a brand-new folder created by whatever user/process this
        // plugin's own process runs as can end up with permission bits that deny
        // write access to the different user/process that originally added this
        // movie to the library (e.g. over SMB) - matching the movie file's own
        // already-correct permissions/group avoids that regardless of which side
        // created which file.
        UnixPermissions.MatchTo(targetDir, localPath, logger, fixPermissions);

        var newLocalPath = localPath;
        var movedCount = 0;
        foreach (var name in filesToMove)
        {
            var src = Path.Combine(currentDir, name);
            var dst = Path.Combine(targetDir, name);
            if (File.Exists(dst))
            {
                logger.LogWarning(
                    "  > Migration target {Dst} already exists, leaving {Src} in place.",
                    PathDisplay.Relative(dst, libraryRoot),
                    PathDisplay.Relative(src, libraryRoot));
                continue;
            }

            try
            {
                File.Move(src, dst);
                movedCount++;
                if (src == localPath)
                {
                    newLocalPath = dst;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogError(
                    "  > Failed to move {Src} to {Dst}: {Error}",
                    PathDisplay.Relative(src, libraryRoot),
                    PathDisplay.Relative(dst, libraryRoot),
                    e.Message);
            }
        }

        if (movedCount > 0)
        {
            logger.LogInformation("  > Moved {Count} file(s) into own folder: {Stem}/", movedCount, movieStem);
        }

        return (newLocalPath, movedCount > 0);
    }
}
