using System;
using System.IO;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Renders a filesystem path relative to a movie's library root for logging, so log
/// output doesn't repeat the full absolute path (container mount point, library
/// hierarchy, etc.) on every line - just the part that's actually specific to the movie.
/// </summary>
public static class PathDisplay
{
    /// <summary>Returns <paramref name="fullPath"/> relative to <paramref name="libraryRoot"/>, or unchanged if that isn't possible.</summary>
    public static string Relative(string fullPath, string? libraryRoot)
    {
        if (string.IsNullOrEmpty(libraryRoot) || string.IsNullOrEmpty(fullPath))
        {
            return fullPath;
        }

        try
        {
            var relative = Path.GetRelativePath(libraryRoot, fullPath);

            // GetRelativePath returns an unrelated-looking result (starting with "..")
            // when fullPath isn't actually under libraryRoot - fall back to the full
            // path rather than showing something confusing.
            if (relative.StartsWith("..", StringComparison.Ordinal))
            {
                return fullPath;
            }

            if (relative != ".")
            {
                return relative;
            }

            // A movie sitting directly in the library root (no subfolder) makes
            // fullPath == libraryRoot, so GetRelativePath returns "." - not useful on
            // its own in a log line ("*** Entering directory: ."). Show the library
            // root's own folder name instead, the same way every other directory here
            // is shown by its own name rather than a path fragment.
            var rootName = Path.GetFileName(libraryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(rootName) ? libraryRoot : rootName;
        }
        catch (ArgumentException)
        {
            return fullPath;
        }
    }
}
