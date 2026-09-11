using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Reads a video file's frame height via ffprobe - used to compare an existing local
/// trailer's resolution against a freshly downloaded candidate when
/// <see cref="Configuration.PluginConfiguration.UpgradeLowQualityTrailers"/> is
/// enabled. A dedicated, minimal probe rather than Jellyfin's own
/// IMediaEncoder.GetMediaInfo: that API is built around a full MediaSourceInfo/
/// EncodingJobInfo request shape meant for playback decisions, more machinery than a
/// one-off "what's this file's height" check needs.
/// </summary>
public static class VideoProbe
{
    /// <summary>Returns the height (in pixels) of the first video stream in <paramref name="filePath"/>, or null if it can't be determined.</summary>
    public static async Task<int?> GetHeightAsync(string ffprobePath, string filePath, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(ffprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-select_streams");
            psi.ArgumentList.Add("v:0");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("stream=height");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add(filePath);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("streams", out var streams) &&
                streams.GetArrayLength() > 0 &&
                streams[0].TryGetProperty("height", out var height))
            {
                return height.GetInt32();
            }

            return null;
        }
        catch (Exception ex)
        {
            // Best-effort: any failure here (ffprobe missing, corrupt/partial file,
            // unexpected output shape) should just mean "resolution unknown", handled
            // identically by every caller - not worth enumerating every possible
            // exception type from launching an external process and parsing its output.
            logger.LogWarning("  > Could not determine resolution of {Path}: {Error}", filePath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Returns the overall duration (in seconds) of <paramref name="filePath"/>, or
    /// null if it can't be determined. Used to cross-check a candidate metadata
    /// match's own claimed runtime (<see cref="MissingMetadataMatcher"/>) against the
    /// actual local file - deliberately read directly from the file via ffprobe
    /// rather than trusted from Jellyfin's own already-scanned RunTimeTicks, since an
    /// item this check applies to (one Jellyfin couldn't match to begin with) is
    /// exactly the case where that cached value is least likely to be trustworthy.
    /// </summary>
    public static async Task<double?> GetDurationSecondsAsync(string ffprobePath, string filePath, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(ffprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add(filePath);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var duration) &&
                double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return seconds;
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("  > Could not determine duration of {Path}: {Error}", filePath, ex.Message);
            return null;
        }
    }
}
