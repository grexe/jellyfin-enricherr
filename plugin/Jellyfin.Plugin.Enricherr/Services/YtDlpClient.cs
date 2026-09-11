using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>A single search/probe result: lightweight metadata without a download.</summary>
/// <param name="Title">The video's title, as reported by the extractor.</param>
/// <param name="DurationSeconds">The video's duration in seconds, if known.</param>
/// <param name="WebpageUrl">The video's canonical webpage URL, used for the follow-up download.</param>
public record YtDlpCandidate(string Title, double? DurationSeconds, string WebpageUrl);

/// <summary>
/// Shells out to the yt-dlp executable to probe candidate videos (metadata only, no
/// download) and to download a chosen one. yt-dlp is not bundled with the plugin -
/// it must be installed wherever the Jellyfin server process itself runs.
///
/// Two-phase probe-then-download, rather than yt-dlp's own Python-API match_filter
/// callback (not available via the CLI): a probe fully extracts metadata for every
/// candidate in a source (e.g. the top 5 "ytsearch5:" results) without downloading, the
/// caller filters candidates in-process (see TitleMatching), and only the accepted
/// candidate is downloaded - functionally equivalent bandwidth-wise, since yt-dlp's own
/// match_filter also fully extracts metadata before deciding to skip a download.
/// </summary>
public class YtDlpClient
{
    private readonly string _ytDlpExecutable;
    private readonly string? _denoPath;
    private readonly string? _cookiesFilePath;
    private readonly string? _ffmpegDir;
    private readonly TimeSpan _requestDelay;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="YtDlpClient"/> class.
    /// </summary>
    /// <param name="ytDlpExecutable">The resolved yt-dlp command or path to invoke (managed, or the admin's own override).</param>
    /// <param name="denoPath">Path to a managed deno executable, if one was provisioned; null to fall back to a bare "deno"/"node" PATH lookup.</param>
    /// <param name="cookiesFilePath">Path to a Netscape-format cookies.txt, if configured.</param>
    /// <param name="ffmpegDir">Directory containing Jellyfin's own ffmpeg binary, reused by yt-dlp for muxing.</param>
    /// <param name="requestDelaySeconds">Minimum seconds to wait before each yt-dlp invocation, to avoid tripping YouTube's own rate limiting across a large run.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public YtDlpClient(string ytDlpExecutable, string? denoPath, string? cookiesFilePath, string? ffmpegDir, int requestDelaySeconds, ILogger logger)
    {
        _ytDlpExecutable = ytDlpExecutable;
        _denoPath = denoPath;
        _cookiesFilePath = cookiesFilePath;
        _ffmpegDir = ffmpegDir;
        _requestDelay = TimeSpan.FromSeconds(Math.Max(0, requestDelaySeconds));
        _logger = logger;
    }

    private List<string> CommonArgs()
    {
        var args = new List<string>
        {
            "--ignore-config",
            "--no-playlist",
            "--no-check-certificates",
            "--geo-bypass",
            "--socket-timeout", "15",
            "--remote-components", "ejs:github"
        };

        // A managed deno's exact path is passed explicitly so it works regardless of
        // PATH; otherwise fall back to a bare PATH lookup (deno first, node as a
        // secondary fallback) in case the admin installed one manually.
        if (!string.IsNullOrEmpty(_denoPath))
        {
            args.Add("--js-runtimes");
            args.Add($"deno:{_denoPath}");
        }
        else
        {
            args.Add("--js-runtimes");
            args.Add("deno");
            args.Add("--js-runtimes");
            args.Add("node");
        }

        // No player_client override here at all, even when cookies are configured -
        // unconditionally forcing "web" for every request whenever a cookies file
        // exists was tried (0.8.12.0) and confirmed live to reintroduce the exact
        // "Only images are available for download" failure 0.8.10.0 removed this
        // override to fix, for videos that have nothing to do with age-verification.
        // Cookies only matter for the (comparatively rare) subset of requests that
        // actually hit an age/sign-in wall; DownloadAsync now handles that as a
        // targeted retry tier instead (forcing player_client=web only after the
        // default selection has already failed), so most requests still get yt-dlp's
        // full, unrestricted, actively-maintained client selection.
        var hasCookies = !string.IsNullOrEmpty(_cookiesFilePath) && File.Exists(_cookiesFilePath);
        if (hasCookies)
        {
            args.Add("--cookies");
            args.Add(_cookiesFilePath!);
        }

        if (!string.IsNullOrEmpty(_ffmpegDir))
        {
            args.Add("--ffmpeg-location");
            args.Add(_ffmpegDir);
        }

        return args;
    }

    /// <summary>Fully extract metadata (title, duration, webpage_url) for every entry in a source, without downloading.</summary>
    public async Task<List<YtDlpCandidate>> ProbeAsync(string source, CancellationToken cancellationToken)
    {
        // A probe only needs title/duration/webpage_url - it never downloads anything -
        // but yt-dlp still resolves a format internally even for "-j --skip-download",
        // and hard-fails the whole extraction if it can't (e.g. "Requested format is
        // not available"), discarding metadata that was otherwise fully extracted.
        // --ignore-no-formats-error keeps that metadata instead of losing a real,
        // possibly-matching candidate over a format problem that doesn't matter yet -
        // DownloadAsync (which does need a real format) still fails normally if this
        // candidate turns out to not actually be downloadable.
        var args = new List<string> { "-j", "--skip-download", "--ignore-no-formats-error" };
        args.AddRange(CommonArgs());
        args.Add(source);

        var (exitCode, stdout, stderr) = await RunAsync(args, cancellationToken).ConfigureAwait(false);

        var candidates = new List<YtDlpCandidate>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                double? duration = root.TryGetProperty("duration", out var d) && d.ValueKind is JsonValueKind.Number
                    ? d.GetDouble()
                    : null;
                var url = root.TryGetProperty("webpage_url", out var w)
                    ? w.GetString()
                    : root.TryGetProperty("original_url", out var ou)
                        ? ou.GetString()
                        : null;

                if (!string.IsNullOrEmpty(url))
                {
                    candidates.Add(new YtDlpCandidate(title, duration, url));
                }
            }
            catch (JsonException e)
            {
                _logger.LogWarning("  > Could not parse yt-dlp JSON output line: {Error}", e.Message);
            }
        }

        if (candidates.Count == 0 && exitCode != 0)
        {
            var stderrLines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var firstError = stderrLines.FirstOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal));
            _logger.LogWarning("  > Source {Source} failed ({Error}). Trying next source...", source, firstError ?? $"exit code {exitCode}");
            foreach (var warningLine in stderrLines.Where(l => l.Contains("WARNING", StringComparison.Ordinal)))
            {
                _logger.LogWarning("  > {WarningLine}", warningLine);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Download a specific video URL into <paramref name="destinationPath"/>, in up to
    /// three tiers:
    ///
    /// 1. yt-dlp's own default client selection, no override - best chance at a
    ///    higher-quality adaptive format when one is actually downloadable, and works
    ///    for the vast majority of (non-age-restricted) videos.
    /// 2. If that fails AND a cookies file is configured, retry with
    ///    player_client=web specifically. A cookies file only actually takes effect on
    ///    the "web" client - the app-style clients (android, ios, mweb, tv,
    ///    android_vr, ...) authenticate via device tokens and silently ignore a
    ///    cookies file regardless of --cookies being set - so this tier exists
    ///    specifically to give an age-restricted video its one shot at using the
    ///    cookies at all. This tier is skipped entirely when no cookies are
    ///    configured, since forcing "web" wouldn't accomplish anything without them.
    /// 3. If that still fails (or there were no cookies to try tier 2 with), retry
    ///    with player_client=mweb. This targets a different, confirmed failure
    ///    pattern: yt-dlp's default selection can pick a client (e.g. "android_vr")
    ///    whose formats extract fine but consistently 403 on the actual download,
    ///    reproduced by hand multiple times. "mweb" doesn't have that problem: its
    ///    adaptive formats get filtered for lacking a PO token, but it then falls back
    ///    cleanly to a legacy, reliably-downloadable format instead of failing
    ///    outright - confirmed reliable across repeated manual attempts.
    ///
    /// Unconditionally forcing "web" for every download whenever cookies were merely
    /// configured (regardless of whether that specific video needed them) was tried
    /// and confirmed live to reintroduce tier 3's "Only images are available for
    /// download" failure for ordinary, non-restricted videos, since it skipped tier 3
    /// entirely in that case - hence tier 2 only ever running as a targeted retry
    /// after tier 1 has already failed, not as the default.
    /// </summary>
    public async Task<bool> DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        if (await DownloadOnceAsync(url, destinationPath, playerClientOverride: null, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var hasCookies = !string.IsNullOrEmpty(_cookiesFilePath) && File.Exists(_cookiesFilePath);
        if (hasCookies)
        {
            _logger.LogInformation("  > Retrying download with player_client=web (cookies configured)...");
            if (await DownloadOnceAsync(url, destinationPath, playerClientOverride: "web", cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        // Confirmed by hand: mweb's adaptive (high-quality) formats need a GVS PO
        // token this plugin doesn't provide, so yt-dlp silently drops them and falls
        // back to an old muxed "18"-style format capped around 360p - reliable, but a
        // real quality cliff from what tier 1 would have gotten. Worth calling out
        // here specifically, since a trailer that ends up unexpectedly low-resolution
        // is otherwise indistinguishable in the log from a normal successful download.
        _logger.LogInformation(
            "  > Retrying download with a more conservative client (mweb) - reliable, but may fall back to a much lower resolution (~360p) than a normal download would get...");
        return await DownloadOnceAsync(url, destinationPath, playerClientOverride: "mweb", cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> DownloadOnceAsync(string url, string destinationPath, string? playerClientOverride, CancellationToken cancellationToken)
    {
        var tmpDir = Directory.CreateTempSubdirectory("enricherr-");
        try
        {
            var tmpPattern = Path.Combine(tmpDir.FullName, "trailer.%(ext)s");
            var args = new List<string>
            {
                // yt-dlp's own default selector (no -f at all is "bestvideo*+bestaudio/best"),
                // not restricted to [ext=mp4]/[ext=m4a] - YouTube's actual highest-resolution
                // streams are usually VP9/AV1-in-webm only, so an mp4/m4a-only restriction was
                // silently capping quality below what's genuinely available (confirmed live:
                // trailers coming through visibly lower quality than expected). --merge-output-format
                // still remuxes the result into an .mp4 container regardless of source codec,
                // matching the fixed ".mp4" trailer filename - Jellyfin transcodes on the fly for
                // any client that can't direct-play the resulting codec, same as it does for
                // every other video in the library.
                "-f", "bestvideo*+bestaudio/best",
                "--merge-output-format", "mp4",
                "--no-part",
                "-o", tmpPattern
            };
            args.AddRange(CommonArgs());
            if (playerClientOverride is not null)
            {
                args.Add("--extractor-args");
                args.Add($"youtube:player_client={playerClientOverride}");
            }
            args.Add(url);

            var (_, _, stderr) = await RunAsync(args, cancellationToken).ConfigureAwait(false);

            var downloadedFile = Directory.EnumerateFiles(tmpDir.FullName)
                .Where(f => new FileInfo(f).Length > 0)
                .FirstOrDefault();

            if (downloadedFile is null)
            {
                var stderrLines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var firstError = stderrLines.FirstOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal));
                _logger.LogWarning("  > No file downloaded for {Url} ({Error}).", url, firstError ?? "unknown error");

                // A "no formats" error's real cause is usually in the WARNING lines
                // right before it (e.g. a client's formats being skipped for lacking a
                // PO token, or a JS-runtime problem preventing signature deciphering) -
                // surfacing only the final ERROR line hides exactly the detail needed
                // to tell "this video genuinely has nothing downloadable" apart from
                // "something about this server's yt-dlp/deno setup isn't working".
                foreach (var warningLine in stderrLines.Where(l => l.Contains("WARNING", StringComparison.Ordinal)))
                {
                    _logger.LogWarning("  > {WarningLine}", warningLine);
                }

                return false;
            }

            var tmpDest = destinationPath + ".part";
            try
            {
                File.Copy(downloadedFile, tmpDest, overwrite: true);
                File.Move(tmpDest, destinationPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("  > Copy to destination failed: {Error}", e.Message);
                return false;
            }
            finally
            {
                // The same permission/locking condition that could make Copy/Move fail
                // above can just as easily make this cleanup Delete fail too - confirmed
                // live, this exact call is what actually crashed a run (naming the
                // ".part" file) even after the catch above was already handling
                // Copy/Move failures, since a finally block's own exception isn't
                // caught by its try's catch clauses.
                try
                {
                    if (File.Exists(tmpDest))
                    {
                        File.Delete(tmpDest);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("  > Could not remove leftover temp file {Path}: {Error}", tmpDest, ex.Message);
                }
            }

            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
            {
                // Not necessarily the final kept result - the caller (DownloadBestAsync)
                // may still discard this in favor of a later, better candidate, and logs
                // its own message once that's decided. This just confirms the download
                // itself succeeded.
                _logger.LogInformation("  > Downloaded: {Name}", Path.GetFileName(destinationPath));
                return true;
            }

            _logger.LogWarning("  > Copy failed or file is empty on remote: {Path}", destinationPath);
            return false;
        }
        finally
        {
            try
            {
                tmpDir.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; a leftover temp dir is harmless.
            }
        }
    }

    /// <summary>
    /// Download a specific video URL's audio only into <paramref name="destinationPath"/>,
    /// extracted to mp3 - used for theme songs (see <see cref="ThemerrDbClient"/>), not
    /// trailers. Uses the same three-tier client fallback as <see cref="DownloadAsync"/>
    /// and for the same reasons (age-restriction via cookies, then a conservative
    /// fallback client) - kept as a fully separate method rather than parametrizing
    /// DownloadAsync/DownloadOnceAsync, so this new, less-tested path can't regress the
    /// existing trailer download path.
    /// </summary>
    public async Task<bool> DownloadAudioAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        if (await DownloadAudioOnceAsync(url, destinationPath, playerClientOverride: null, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var hasCookies = !string.IsNullOrEmpty(_cookiesFilePath) && File.Exists(_cookiesFilePath);
        if (hasCookies)
        {
            _logger.LogInformation("  > Retrying theme song download with player_client=web (cookies configured)...");
            if (await DownloadAudioOnceAsync(url, destinationPath, playerClientOverride: "web", cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        _logger.LogInformation("  > Retrying theme song download with a more conservative client (mweb)...");
        return await DownloadAudioOnceAsync(url, destinationPath, playerClientOverride: "mweb", cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> DownloadAudioOnceAsync(string url, string destinationPath, string? playerClientOverride, CancellationToken cancellationToken)
    {
        var tmpDir = Directory.CreateTempSubdirectory("enricherr-theme-");
        try
        {
            var tmpPattern = Path.Combine(tmpDir.FullName, "theme.%(ext)s");
            var args = new List<string>
            {
                "-f", "bestaudio/best",
                "--extract-audio",
                "--audio-format", "mp3",
                "--no-part",
                "-o", tmpPattern
            };
            args.AddRange(CommonArgs());
            if (playerClientOverride is not null)
            {
                args.Add("--extractor-args");
                args.Add($"youtube:player_client={playerClientOverride}");
            }

            args.Add(url);

            var (_, _, stderr) = await RunAsync(args, cancellationToken).ConfigureAwait(false);

            var downloadedFile = Directory.EnumerateFiles(tmpDir.FullName)
                .Where(f => new FileInfo(f).Length > 0)
                .FirstOrDefault();

            if (downloadedFile is null)
            {
                var stderrLines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var firstError = stderrLines.FirstOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal));
                _logger.LogWarning("  > No theme song downloaded for {Url} ({Error}).", url, firstError ?? "unknown error");
                foreach (var warningLine in stderrLines.Where(l => l.Contains("WARNING", StringComparison.Ordinal)))
                {
                    _logger.LogWarning("  > {WarningLine}", warningLine);
                }

                return false;
            }

            var tmpDest = destinationPath + ".part";
            try
            {
                File.Copy(downloadedFile, tmpDest, overwrite: true);
                File.Move(tmpDest, destinationPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("  > Copy to destination failed: {Error}", e.Message);
                return false;
            }
            finally
            {
                // The same permission/locking condition that could make Copy/Move fail
                // above can just as easily make this cleanup Delete fail too - confirmed
                // live, this exact call is what actually crashed a run (naming the
                // ".part" file) even after the catch above was already handling
                // Copy/Move failures, since a finally block's own exception isn't
                // caught by its try's catch clauses.
                try
                {
                    if (File.Exists(tmpDest))
                    {
                        File.Delete(tmpDest);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("  > Could not remove leftover temp file {Path}: {Error}", tmpDest, ex.Message);
                }
            }

            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
            {
                _logger.LogInformation("  > Theme song successfully saved: {Name}", Path.GetFileName(destinationPath));
                return true;
            }

            _logger.LogWarning("  > Copy failed or file is empty on remote: {Path}", destinationPath);
            return false;
        }
        finally
        {
            try
            {
                tmpDir.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup; a leftover temp dir is harmless.
            }
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(List<string> args, CancellationToken cancellationToken)
    {
        if (_requestDelay > TimeSpan.Zero)
        {
            await Task.Delay(_requestDelay, cancellationToken).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Append(e.Data).Append('\n'); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Append(e.Data).Append('\n'); };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _logger.LogError(
                "  > Could not launch yt-dlp ({Path}): {Error}",
                _ytDlpExecutable,
                e.Message);
            return (-1, string.Empty, string.Empty);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        var stderrText = stderr.ToString();

        // A distinct, terminal condition, not an ordinary per-candidate failure: once
        // YouTube has rate-limited the session, no client/format fallback will help,
        // and continuing to send requests (to more candidates, or the next
        // movie/series) just wastes time against a block that won't lift itself any
        // faster - possibly making it worse. Thrown here (not just logged) so it
        // propagates straight past every retry tier and the movie/series loop itself.
        if (stderrText.Contains("rate-limited by YouTube", StringComparison.OrdinalIgnoreCase))
        {
            throw new YouTubeRateLimitedException(
                "The current session has been rate-limited by YouTube for up to an hour. Stopping the run " +
                "now. For more details, see " +
                "https://github.com/yt-dlp/yt-dlp/wiki/Extractors#this-content-isnt-available-try-again-later");
        }

        return (process.ExitCode, stdout.ToString(), stderrText);
    }
}
