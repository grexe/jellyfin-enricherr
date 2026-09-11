using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Makes a newly created file/folder as usable by the rest of a library-managing
/// setup as a reference file already is - matching its permission bits and group.
/// Confirmed live: a movie's own folder, created earlier by a different user (e.g.
/// media added over SMB as one user, with Jellyfin's own process running as
/// another), denied this plugin's own process write access to it entirely - the
/// two users shared a group, but the folder's permission bits didn't grant that
/// group write access. Rather than picking one fixed mode to force everywhere,
/// matching whatever the library's own existing files already use adapts to
/// however a given deployment is actually set up.
///
/// Never changes the file's OWNER (uid) - that generally requires root, which this
/// plugin shouldn't need or want. No-op on Windows, where Unix ownership/permission
/// bits don't apply. Otherwise POSIX-portable, not Linux-specific: permission bits
/// use .NET's own cross-platform File.GetUnixFileMode/SetUnixFileMode, and group
/// matching resolves the reference file's numeric group id via `stat` (its format
/// flag differs between BSD/macOS and GNU/Linux, handled below) and passes that
/// plain number to `chgrp` - deliberately not `chgrp --reference=`, which is a
/// GNU-only extension BSD's chgrp (macOS) doesn't support; a bare numeric group id
/// is standard `chgrp` usage everywhere. Best-effort throughout: any failure here
/// is logged and otherwise ignored, never blocking or failing the trailer/theme
/// song fetch that created the file/folder in the first place.
/// </summary>
public static class UnixPermissions
{
    /// <summary>Matches <paramref name="newPath"/>'s permission bits and group to <paramref name="referencePath"/>'s.</summary>
    public static void MatchTo(string newPath, string referencePath, ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(referencePath);
            if (File.Exists(newPath) && !Directory.Exists(newPath))
            {
                // Don't carry over an execute bit onto a regular media file just
                // because the reference happened to be a directory (which needs
                // execute to be traversable) - only meaningful for the reference's
                // own file/directory type, not something to propagate onto a file.
                mode &= ~(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            File.SetUnixFileMode(newPath, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger.LogWarning("  > Could not match permission bits on {Path} to {Reference}: {Error}", newPath, referencePath, ex.Message);
        }

        var gid = GetGroupId(referencePath, logger);
        if (gid is null)
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo("chgrp")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(gid.Value.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(newPath);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return;
            }

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                logger.LogWarning("  > Could not match group ownership on {Path} to {Reference}: {Error}", newPath, referencePath, stderr.Trim());
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // e.g. chgrp isn't installed - best-effort, the file/folder still works
            // for whichever process/user actually created it, just not necessarily
            // for others sharing the reference file's group.
            logger.LogWarning("  > Could not run chgrp for {Path}: {Error}", newPath, ex.Message);
        }
    }

    /// <summary>Reads a file's numeric group id via `stat`, or null on any failure.</summary>
    private static uint? GetGroupId(string path, ILogger logger)
    {
        // BSD stat (macOS/FreeBSD) uses -f with its own format-string syntax; GNU
        // stat (Linux, and BusyBox's GNU-compatible mode) uses -c. "%g" (group id)
        // happens to be the same token in both, confirmed directly against both a
        // real macOS `stat -f %g` and the GNU coreutils manual for `-c %g`.
        var formatFlag = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? "-f" : "-c";

        try
        {
            var psi = new ProcessStartInfo("stat")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(formatFlag);
            psi.ArgumentList.Add("%g");
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0 && uint.TryParse(stdout.Trim(), out var gid))
            {
                return gid;
            }

            logger.LogWarning("  > Could not determine group of {Path}: {Error}", path, stderr.Trim());
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            logger.LogWarning("  > Could not run stat for {Path}: {Error}", path, ex.Message);
            return null;
        }
    }
}
