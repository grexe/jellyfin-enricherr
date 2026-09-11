using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Enricherr.Configuration;
using Jellyfin.Plugin.Enricherr.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.ScheduledTasks;

/// <summary>
/// Scheduled task that finds and downloads missing local trailers for movies and TV
/// series. For each item without one: tries the official RemoteTrailers first, then a
/// multi-stage set of YouTube searches, downloads the first candidate that passes
/// duration/title filtering via yt-dlp. Title/year resolution and source-query building
/// (<see cref="ItemMetadata"/>, <see cref="TrailerSources"/>) are shared between movies
/// and series - both are plain <see cref="BaseItem"/> lookups with no movie- or
/// series-specific behavior. What genuinely differs is kept in separate orchestration
/// methods: movies additionally support renaming the original file and/or migrating it
/// into its own folder (<see cref="MovieFileOperations"/>) - required for Jellyfin to
/// recognize a local trailer at all when movies share a flat folder
/// (see https://github.com/jellyfin/jellyfin/issues/10077) - while a series always
/// already lives in its own dedicated folder, so that step doesn't apply to it at all.
/// </summary>
public class FetchTrailersTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FetchTrailersTask> _logger;
    private readonly LibraryItemsFinder _libraryItemsFinder;

    /// <summary>
    /// Initializes a new instance of the <see cref="FetchTrailersTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface, used to point yt-dlp at Jellyfin's own ffmpeg.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface, used to download managed yt-dlp/deno binaries.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{FetchTrailersTask}"/> interface.</param>
    public FetchTrailersTask(ILibraryManager libraryManager, ILocalizationManager localization, IMediaEncoder mediaEncoder, IHttpClientFactory httpClientFactory, ILogger<FetchTrailersTask> logger)
    {
        _libraryManager = libraryManager;
        _localization = localization;
        _mediaEncoder = mediaEncoder;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _libraryItemsFinder = new LibraryItemsFinder(libraryManager, logger);
    }

    /// <inheritdoc />
    public string Name => "Fetch Theme Music and Trailers";

    /// <inheritdoc />
    // Deliberately unchanged from the plugin's original trailers-only scope - this is
    // Jellyfin's own internal identity for the task's configured schedule/triggers, so
    // changing it would make Jellyfin treat an upgrade as a brand-new task and silently
    // drop any custom trigger a user already configured (e.g. a nightly time).
    public string Key => "FetchMissingTrailers";

    /// <inheritdoc />
    public string Description => "Downloads missing local trailers and theme songs for movies and TV series from YouTube via yt-dlp.";

    /// <inheritdoc />
    // Same localized "TasksLibraryCategory" string the built-in library tasks use
    // (e.g. RefreshMediaLibraryTask) - a literal "Library" only matches in English;
    // on a non-English server it renders as its own untranslated group instead of
    // joining the real "Library" group whose label came from this same key.
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        _logger.LogInformation(
            "Jellyfin Enricherr run starting. RenameOriginal={RenameOriginal}, MigrateToFolders={MigrateToFolders}, " +
            "DryRun={DryRun}, TriggerLibraryScan={TriggerLibraryScan}",
            config.RenameOriginal,
            config.MigrateToFolders,
            config.DryRun,
            config.TriggerLibraryScan);

        var libraryIds = config.LibraryIds ?? Array.Empty<string>();
        var movies = _libraryItemsFinder.GetMovies(libraryIds);
        var series = _libraryItemsFinder.GetSeries(libraryIds);

        _logger.LogInformation("Found {MovieCount} movie(s) and {SeriesCount} series to process.", movies.Count, series.Count);

        string? ffmpegDir = null;
        try
        {
            ffmpegDir = Path.GetDirectoryName(_mediaEncoder.EncoderPath);
        }
        catch (ArgumentException)
        {
            // EncoderPath not configured yet; yt-dlp falls back to its own bundled/PATH ffmpeg.
        }

        string? ffprobePath = null;
        try
        {
            ffprobePath = _mediaEncoder.ProbePath;
        }
        catch (ArgumentException)
        {
            // ProbePath not configured yet; trailer-quality upgrade checks are skipped without it.
        }

        var ytDlp = await BuildYtDlpClientAsync(config, ffmpegDir, cancellationToken).ConfigureAwait(false);
        var themerrDb = new ThemerrDbClient(_httpClientFactory, _logger);
        var stats = new TrailerFetchStats();
        var totalItems = movies.Count + series.Count;
        var startedAt = DateTime.UtcNow;

        // Cancelling a run (e.g. from the dashboard), or YouTube rate-limiting the
        // session, must still leave the summary reflecting whatever was found before
        // it stopped, rather than silently leaving a stale summary from a previous run
        // on display - so both are caught here (not left to propagate straight out of
        // the loops) and the partial summary is logged/persisted either way.
        // Cancellation is then rethrown so Jellyfin's TaskManager still correctly
        // reports the run as cancelled rather than completed; a rate limit isn't an
        // admin-initiated cancellation, so that case completes normally instead (with
        // a clear ERROR-level log line and a distinct stop reason in the summary).
        OperationCanceledException? cancellation = null;
        string? stopReason = null;
        var movieIndex = 0;
        var seriesIndex = 0;
        var hasRetriedRateLimit = false;

        // There's no reliable way to know when YouTube's own rate limit actually
        // lifts (its own message only states an upper bound), so on the first hit
        // this waits once and resumes the SAME run from wherever it stopped (the
        // movie/series indices are tracked outside the try so a retry doesn't
        // restart from scratch) rather than looping/backing off indefinitely - a
        // retry that also gets rate-limited stops the run for good.
        while (true)
        {
            try
            {
                stats.MoviePhaseStarted = true;
                for (; movieIndex < movies.Count; movieIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessMovieAsync(movies[movieIndex], config, ytDlp, themerrDb, stats, ffprobePath, cancellationToken).ConfigureAwait(false);
                    progress.Report((movieIndex + 1) * 100.0 / totalItems);

                    // A movie/series that completes without hitting the rate limit again
                    // is proof the limit actually lifted, not just that we got lucky once
                    // - re-arm the single retry so a *later* rate limit in this same run
                    // (a large backlog can plausibly retrigger it more than once) gets its
                    // own chance to wait-and-resume too, instead of always giving up
                    // immediately after the first retry has ever been used.
                    hasRetriedRateLimit = false;
                }

                stats.SeriesPhaseStarted = true;
                for (; seriesIndex < series.Count; seriesIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessSeriesAsync(series[seriesIndex], config, ytDlp, themerrDb, stats, ffprobePath, cancellationToken).ConfigureAwait(false);
                    progress.Report((movies.Count + seriesIndex + 1) * 100.0 / totalItems);
                    hasRetriedRateLimit = false;
                }

                break;
            }
            catch (OperationCanceledException ex)
            {
                cancellation = ex;
                stopReason = "Cancelled";
                break;
            }
            catch (YouTubeRateLimitedException ex)
            {
                if (config.RetryOnRateLimit && !hasRetriedRateLimit)
                {
                    hasRetriedRateLimit = true;
                    _logger.LogWarning(
                        "{Detail} Waiting {Minutes} minute(s), then retrying the rest of this run once.",
                        ex.Message,
                        config.RateLimitRetryDelayMinutes);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(config.RateLimitRetryDelayMinutes), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException delayEx)
                    {
                        cancellation = delayEx;
                        stopReason = "Cancelled";
                        break;
                    }

                    _logger.LogInformation("Resuming after the rate-limit wait...");
                    continue;
                }

                stopReason = "YouTube rate-limited this session";
                _logger.LogError("{Detail}", ex.Message);
                break;
            }
        }

        LogSummary(stats, movies.Count, series.Count, config.DryRun, startedAt, stopReason);

        if (cancellation is not null)
        {
            _logger.LogInformation("Run cancelled - see the summary above for what was found before it stopped.");
            throw cancellation;
        }

        // Trigger a single library scan (not one per movie/series) so Jellyfin picks up
        // every newly downloaded trailer/theme song file, and any moved/migrated paths,
        // in one pass.
        if (config.TriggerLibraryScan && !config.DryRun)
        {
            var downloaded = stats.Downloaded + stats.SeriesDownloaded;
            var themeSongsDownloaded = stats.ThemeSongDownloaded + stats.SeriesThemeSongDownloaded;
            var renamed = stats.Renamed + stats.SeriesRenamed + stats.SeriesSeasonsRenamed;
            if (downloaded > 0 || stats.Migrated > 0 || themeSongsDownloaded > 0 || renamed > 0)
            {
                _logger.LogInformation(
                    "Triggering a Jellyfin library scan to pick up {Downloaded} new trailer(s), {ThemeSongs} new theme song(s), {Migrated} migrated movie(s), and {Renamed} renamed movie(s)/series folder(s)...",
                    downloaded,
                    themeSongsDownloaded,
                    stats.Migrated,
                    renamed);
                _libraryManager.QueueLibraryScan();
            }
            else
            {
                _logger.LogInformation("No new trailers, theme songs, migrations, or renames; skipping Jellyfin library scan.");
            }
        }
    }

    /// <summary>
    /// Resolves the yt-dlp and deno executables to use, downloading and managing both
    /// via <see cref="DependencyProvisioner"/> - always the plugin's own tested copies,
    /// never a system installation, so there's no server/container customization or
    /// version-mismatch support burden. A dry run never actually invokes yt-dlp (see
    /// ProcessMovieAsync), so provisioning is skipped entirely then, keeping dry-run
    /// free of side effects and instant.
    /// </summary>
    private async Task<YtDlpClient> BuildYtDlpClientAsync(PluginConfiguration config, string? ffmpegDir, CancellationToken cancellationToken)
    {
        if (config.DryRun)
        {
            return new YtDlpClient("yt-dlp", denoPath: null, config.CookiesFilePath, ffmpegDir, config.RequestDelaySeconds, _logger);
        }

        var provisioner = new DependencyProvisioner(_httpClientFactory, Plugin.Instance!.DataFolderPath, _logger);
        var managedYtDlp = await provisioner.EnsureYtDlpAsync(cancellationToken).ConfigureAwait(false);
        if (managedYtDlp is null)
        {
            _logger.LogError("Could not automatically provision yt-dlp; no trailers can be fetched this run.");
            return new YtDlpClient("yt-dlp", denoPath: null, config.CookiesFilePath, ffmpegDir, config.RequestDelaySeconds, _logger);
        }

        var managedDeno = await provisioner.EnsureDenoAsync(cancellationToken).ConfigureAwait(false);
        if (managedDeno is null)
        {
            // Not fatal - yt-dlp falls back to a bare "deno"/"node" PATH lookup, which
            // usually finds nothing on a stock container - but many videos only need
            // deno-based signature deciphering for their higher-quality formats, so
            // this can silently turn into "every download fails with 'no formats
            // available'" instead of a clear, attributable error. Worth a clear log
            // line rather than only showing up as a downstream symptom.
            _logger.LogWarning(
                "Could not provision deno; yt-dlp will fall back to a bare PATH lookup for a JS runtime, which " +
                "will likely find nothing. Videos needing signature deciphering for their formats may fail to download.");
        }

        _logger.LogInformation("Using yt-dlp: {YtDlpPath}, deno: {DenoPath}", managedYtDlp, managedDeno ?? "(not available)");
        return new YtDlpClient(managedYtDlp, managedDeno, config.CookiesFilePath, ffmpegDir, config.RequestDelaySeconds, _logger);
    }

    private async Task ProcessMovieAsync(Movie movie, PluginConfiguration config, YtDlpClient ytDlp, ThemerrDbClient themerrDb, TrailerFetchStats stats, string? ffprobePath, CancellationToken cancellationToken)
    {
        var rawTitle = string.IsNullOrEmpty(movie.Name) ? "Unknown" : movie.Name;
        var localPath = movie.Path;

        if (string.IsNullOrEmpty(localPath))
        {
            stats.Skipped++;
            return;
        }

        // The library's own root folder (e.g. "/media/Anime/Movies"), so paths in the
        // log can be shown relative to it instead of repeating the full container path
        // (mount point, library hierarchy) on every line.
        var libraryRoot = movie.GetTopParent()?.Path;

        if (!MovieFileOperations.IsValidMediaFile(localPath, out var reason))
        {
            _logger.LogWarning("Skipping {Title}: {Reason} ({Path})", rawTitle, reason, PathDisplay.Relative(localPath, libraryRoot));
            stats.Skipped++;
            return;
        }

        // A trailer saved next to a movie that shares its folder with other movies
        // (no dedicated folder of its own) would just sit there unrecognized by
        // Jellyfin (https://github.com/jellyfin/jellyfin/issues/10077) - confirmed
        // live: several older, flat-structured movies had a "<title>-trailer.mp4"
        // sitting in the shared library root right next to unrelated movies' own
        // files, invisible to Jellyfin's local-trailer resolution. Only a problem
        // when folder migration is off - "TrailersOnly"/"All" would move this movie
        // into its own folder later in this same run/a future one, making the
        // trailer valid once migrated.
        if (config.MigrateToFolders == MigrationMode.Disabled && !MovieFileOperations.HasOwnFolder(localPath))
        {
            _logger.LogWarning(
                "Skipping {Title}: not in its own dedicated folder, and \"Migrate movies into their own folder\" " +
                "is off - a trailer saved here would sit in a folder shared with other movies, which Jellyfin " +
                "can't recognize as belonging to this one. Enable folder migration, or move this movie into its " +
                "own folder manually. ({Path})",
                rawTitle,
                PathDisplay.Relative(localPath, libraryRoot));
            stats.Skipped++;
            return;
        }

        stats.Scanned++;

        var (preferredTitle, titleVariants) = ItemMetadata.ResolveTitles(movie, localPath);
        var folderPath = Path.GetDirectoryName(localPath) ?? string.Empty;

        if (stats.LastDir != folderPath)
        {
            _logger.LogInformation("*** Entering directory: {Dir}", PathDisplay.Relative(folderPath, libraryRoot));
            stats.LastDir = folderPath;
        }

        _logger.LogInformation("Processing movie file: {Name} ...", Path.GetFileName(localPath));

        var year = ItemMetadata.ResolveYear(movie, localPath);
        var yearStr = year is not null ? $" ({year})" : string.Empty;
        var safeTitle = TitleMatching.SanitizeFilename($"{preferredTitle}{yearStr}");

        _logger.LogInformation("  > using title {Title}", preferredTitle);
        var trailerFilename = Path.Combine(folderPath, $"{safeTitle}-trailer.mp4");

        var movieDurationSec = movie.RunTimeTicks.HasValue ? movie.RunTimeTicks.Value / 10_000_000.0 : (double?)null;

        var trailerCandidates = new[]
        {
            trailerFilename,
            Path.Combine(folderPath, $"{safeTitle}-trailer.mkv"),
            Path.Combine(folderPath, "trailer.mp4"),
            Path.Combine(folderPath, "trailer.mkv")
        };
        var existingTrailerPath = trailerCandidates.FirstOrDefault(File.Exists);
        var alreadyHadTrailer = movie.LocalTrailers.Count > 0 || existingTrailerPath is not null;
        var downloadSuccess = false;

        if (alreadyHadTrailer)
        {
            stats.AlreadyHadTrailer++;
        }

        // An existing trailer still gets a search/download attempt if it's below the
        // configured minimum resolution and upgrades are enabled - the original file
        // is only ever replaced by a genuinely higher-resolution download (see the
        // ResolveTrailerUpgrade call below), so a re-attempt that can't beat it never
        // makes things worse.
        var (shouldSearch, upgradeBackupPath, existingHeight) = await PrepareUpgradeAttemptAsync(
            alreadyHadTrailer, existingTrailerPath, config, ffprobePath, cancellationToken).ConfigureAwait(false);

        if (alreadyHadTrailer && !shouldSearch)
        {
            _logger.LogInformation("  > Trailer already exists, skipping.");
        }

        if (shouldSearch)
        {
            if (config.RenameOriginal)
            {
                var (newPath, renamed) = MovieFileOperations.RenameMovieFile(localPath, safeTitle, config.DryRun, libraryRoot, _logger);
                if (renamed)
                {
                    stats.Renamed++;
                    localPath = newPath;
                    folderPath = Path.GetDirectoryName(localPath) ?? string.Empty;
                    trailerFilename = Path.Combine(folderPath, $"{safeTitle}-trailer.mp4");
                }
            }

            // Same permission-drift healing as the theme song folder below - a folder
            // that already existed before this run (not freshly migrated by
            // MigrateToOwnFolder above) never otherwise gets its permissions checked
            // before we try to write a trailer into it.
            UnixPermissions.MatchTo(folderPath, localPath, _logger);

            // Resolution/audio preference are honored on every search, not just an
            // upgrade re-check (see DownloadBestAsync) - so a fresh item doesn't need
            // a later "Update existing trailers" run just to reach quality it could
            // have gotten immediately.
            var sourcesToTry = TrailerSources.Build(movie, titleVariants, year, skipNativeLanguage: config.AllowUpgradeInOtherLanguage);

            var (bestSuccess, bestHeight) = await DownloadBestAsync(
                sourcesToTry, trailerFilename, titleVariants, movieDurationSec, localPath, config, ffprobePath, ytDlp, cancellationToken).ConfigureAwait(false);
            downloadSuccess = bestSuccess;

            if (upgradeBackupPath is not null)
            {
                downloadSuccess = ResolveTrailerUpgrade(downloadSuccess, bestHeight, trailerFilename, existingTrailerPath!, upgradeBackupPath, existingHeight);
                if (downloadSuccess)
                {
                    stats.Upgraded++;
                    UnixPermissions.MatchTo(trailerFilename, localPath, _logger);
                }
            }
            else if (downloadSuccess)
            {
                stats.Downloaded++;
            }
            else
            {
                stats.NotFound++;
            }
        }

        // Jellyfin's local-extras resolver silently ignores a correctly-named
        // "<title>-trailer" file sitting in a folder shared by multiple movies - it only
        // recognizes one when the movie has its own folder
        // (https://github.com/jellyfin/jellyfin/issues/10077). "All" migrates every
        // movie; "TrailersOnly" only migrates movies that actually have a trailer
        // (pre-existing or just downloaded), leaving the rest of a flat library untouched.
        var shouldMigrate = config.MigrateToFolders == MigrationMode.All ||
                             (config.MigrateToFolders == MigrationMode.TrailersOnly && (alreadyHadTrailer || downloadSuccess));
        var themeSongFolder = folderPath;
        if (shouldMigrate)
        {
            var (newLocalPath, moved) = MovieFileOperations.MigrateToOwnFolder(localPath, config.DryRun, [safeTitle], libraryRoot, _logger);
            if (moved)
            {
                stats.Migrated++;
                localPath = newLocalPath;
                themeSongFolder = Path.GetDirectoryName(newLocalPath) ?? folderPath;
            }
        }

        // Unlike "<title>-trailer.ext", "theme.mp3" is a fixed filename with no
        // per-movie disambiguation - downloading it into a folder shared by other
        // movies wouldn't just go unrecognized by Jellyfin like an un-migrated trailer
        // does, it would actively misattribute one movie's theme song to every other
        // movie sharing that folder (first one processed "wins" the shared file,
        // every other movie sees "already had" and never gets its own). So this only
        // runs once the movie is verifiably in its own dedicated folder - after
        // migration (if any) this run, so a theme song downloaded now lands in the
        // movie's final folder directly rather than needing to be swept along by
        // MigrateToOwnFolder, which wouldn't recognize "theme.mp3" as this movie's
        // file anyway (not tied to its title stem the way IsSidecarOf checks for).
        var movieHasOwnFolder = string.Equals(
            Path.GetFileName(themeSongFolder),
            Path.GetFileNameWithoutExtension(localPath),
            StringComparison.Ordinal);

        // Confirmed live: a folder migrated in an *earlier* run never gets its
        // permissions healed by the block above, since MigrateToOwnFolder's own
        // "already in its own folder" check returns early before ever reaching its
        // UnixPermissions.MatchTo call - only a folder created in *this exact* run
        // benefited. Re-applying it here, every run, for any movie that already has
        // its own folder (not just a freshly migrated one) fixes that: permission
        // drift the folder had from however/whenever it was actually created gets
        // healed before writing a theme song into it, not just once at creation time.
        if (config.FetchThemeSongs && movieHasOwnFolder)
        {
            UnixPermissions.MatchTo(themeSongFolder, localPath, _logger);
        }

        if (config.FetchThemeSongs && !movieHasOwnFolder)
        {
            _logger.LogInformation(
                "  > Skipping theme song: movie isn't in its own dedicated folder (see \"Migrate movies into their own folder\") - a shared folder would misattribute theme.mp3 between movies.");
            ApplyThemeSongOutcome(ThemeSongOutcome.NotFound, stats, isSeries: false);
        }
        else
        {
            var themeOutcome = await FetchThemeSongAsync(movie, themeSongFolder, isSeries: false, config, ytDlp, themerrDb, cancellationToken).ConfigureAwait(false);
            ApplyThemeSongOutcome(themeOutcome, stats, isSeries: false);
        }
    }

    /// <summary>
    /// Decides whether an item with an existing local trailer should still get a
    /// search/download attempt because it's below <see cref="PluginConfiguration.MinTrailerResolution"/>,
    /// and if so, moves the existing file aside so the normal download flow can write
    /// a fresh one without clobbering it before the two are compared by
    /// <see cref="ResolveTrailerUpgrade"/>. Shared between movies and series -
    /// this decision has no movie/series-specific behavior of its own.
    /// </summary>
    /// <returns>
    /// ShouldSearch: true if a search/download attempt should run (either there was no
    /// trailer at all, or an upgrade attempt is warranted). UpgradeBackupPath: where the
    /// existing trailer was moved to, non-null only for an upgrade attempt.
    /// ExistingHeight: the existing trailer's probed resolution, if known.
    /// </returns>
    private async Task<(bool ShouldSearch, string? UpgradeBackupPath, int? ExistingHeight)> PrepareUpgradeAttemptAsync(
        bool alreadyHadTrailer, string? existingTrailerPath, PluginConfiguration config, string? ffprobePath, CancellationToken cancellationToken)
    {
        if (!alreadyHadTrailer)
        {
            return (true, null, null);
        }

        if (!config.UpgradeLowQualityTrailers || config.DryRun || existingTrailerPath is null || string.IsNullOrEmpty(ffprobePath))
        {
            return (false, null, null);
        }

        var existingHeight = await VideoProbe.GetHeightAsync(ffprobePath, existingTrailerPath, _logger, cancellationToken).ConfigureAwait(false);
        if (existingHeight is null || existingHeight.Value >= config.MinTrailerResolution)
        {
            return (false, null, existingHeight);
        }

        _logger.LogInformation(
            "  > Existing trailer is only {Height}p (below the configured minimum of {Min}p) - looking for a better one...",
            existingHeight,
            config.MinTrailerResolution);

        var upgradeBackupPath = existingTrailerPath + ".upgrading";
        try
        {
            File.Move(existingTrailerPath, upgradeBackupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // e.g. the file is locked because it's actively being streamed right now -
            // skip the upgrade attempt for this one item rather than letting an
            // unhandled exception here abort the entire remaining run.
            _logger.LogWarning("  > Could not set aside the existing trailer for an upgrade attempt, skipping: {Error}", ex.Message);
            return (false, null, existingHeight);
        }

        return (true, upgradeBackupPath, existingHeight);
    }

    /// <summary>
    /// Tries each source in order, keeping the highest-resolution successful download
    /// found across the whole attempt - the same "only keep a genuine improvement" rule
    /// <see cref="ResolveTrailerUpgrade"/> applies when comparing against a
    /// pre-existing trailer. Each candidate downloads to a scratch path first and is
    /// only moved into <paramref name="trailerFilename"/> - the live path Jellyfin's own
    /// file-watcher/metadata pipeline monitors - via a single atomic move once it's
    /// confirmed as the new best; a candidate that isn't better is discarded without
    /// trailerFilename ever being touched. Confirmed live: writing every attempt
    /// directly to trailerFilename (an earlier version of this method) made Jellyfin's
    /// own background metadata refresh race an ffprobe against ours mid-write, and
    /// against the gap a delete-then-restore briefly left, producing real "ffprobe
    /// failed - streams and format are both null" errors in Jellyfin's own log. Stops
    /// as soon as a result meets <see cref="PluginConfiguration.MinTrailerResolution"/>,
    /// or once every source has been tried. This runs the same way regardless of
    /// whether the item already had a trailer before this run - resolution and
    /// audio-language preference are meant to be honored on every search, not just an
    /// upgrade re-check, so a fresh item doesn't need a later "Update existing
    /// trailers" run just to reach quality it could have gotten immediately. Shared
    /// between movies and series.
    /// </summary>
    /// <returns>Whether a trailer was saved this run, and its probed height if known.</returns>
    private async Task<(bool Success, int? Height)> DownloadBestAsync(
        IReadOnlyList<string> sourcesToTry,
        string trailerFilename,
        List<string> titleVariants,
        double? itemDurationSeconds,
        string localPath,
        PluginConfiguration config,
        string? ffprobePath,
        YtDlpClient ytDlp,
        CancellationToken cancellationToken)
    {
        // "[DRY-RUN] " is baked into the template text itself (per branch) rather than
        // passed as a {Prefix} value - splicing a text fragment in through a
        // structured-logging placeholder gets it quoted on its own by the logging
        // backend (the same class of bug fixed previously for a pluralization suffix),
        // which reads badly for a fragment that's sometimes empty.
        var fetchingTemplate = config.DryRun
            ? "  > [DRY-RUN] Fetching trailer via {Kind} ({Source})..."
            : "  > Fetching trailer via {Kind} ({Source})...";

        var success = false;
        int? bestHeight = null;

        foreach (var source in sourcesToTry)
        {
            var isSearch = source.StartsWith("ytsearch", StringComparison.Ordinal);
            _logger.LogInformation(fetchingTemplate, isSearch ? "Search" : "Remote-URL", source);

            if (config.DryRun)
            {
                _logger.LogInformation("  > [DRY-RUN] Will save as: {Name}", Path.GetFileName(trailerFilename));
                return (true, null);
            }

            var candidates = await ytDlp.ProbeAsync(source, cancellationToken).ConfigureAwait(false);

            YtDlpCandidate? accepted = null;
            foreach (var candidate in candidates)
            {
                if (TrailerCandidateFilter.Accept(candidate, titleVariants, itemDurationSeconds, isSearch, config.MaxTrailerDurationSeconds, out var rejectReason))
                {
                    accepted = candidate;
                    break;
                }

                if (config.VerboseLogging)
                {
                    _logger.LogInformation("  > [filter] {Reason}", rejectReason);
                }
            }

            if (accepted is null)
            {
                _logger.LogWarning("  > No suitable trailer found for source ({Source}).", source);
                continue;
            }

            // Downloaded to the OS temp directory, never anywhere inside the watched
            // media library - Jellyfin's own file-watcher/metadata pipeline monitors
            // that folder, and racing its own ffprobe against ours mid-write (or
            // against the brief gap a delete-then-restore would leave) produced real
            // "ffprobe failed - streams and format are both null" errors in Jellyfin's
            // own log, confirmed live. A rejected candidate never touches the library
            // at all now; trailerFilename is only ever touched once, via File.Move (a
            // same-filesystem rename when possible, a transparent copy+delete
            // otherwise - .NET picks whichever applies), at the exact moment a
            // candidate is confirmed kept.
            var candidatePath = Path.Combine(Path.GetTempPath(), $"enricherr-candidate-{Guid.NewGuid():N}{Path.GetExtension(trailerFilename)}");
            var attemptSuccess = await ytDlp.DownloadAsync(accepted.WebpageUrl, candidatePath, cancellationToken).ConfigureAwait(false);
            if (!attemptSuccess)
            {
                continue;
            }

            var attemptHeight = string.IsNullOrEmpty(ffprobePath)
                ? null
                : await VideoProbe.GetHeightAsync(ffprobePath, candidatePath, _logger, cancellationToken).ConfigureAwait(false);

            var isBetter = bestHeight is null || (attemptHeight is not null && attemptHeight.Value > bestHeight.Value);
            if (!isBetter)
            {
                // bestHeight is never null here (isBetter's first clause already
                // covers a null bestHeight), so this is always a genuine comparison,
                // not a "resolution unknown" case.
                _logger.LogInformation(
                    "  > This candidate is {Height}p, not better than the best found so far ({Best}p) - discarding, still searching...",
                    attemptHeight,
                    bestHeight);

                try
                {
                    File.Delete(candidatePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("  > Could not discard a non-improving trailer attempt {Path}: {Error}", candidatePath, ex.Message);
                }

                continue;
            }

            try
            {
                File.Move(candidatePath, trailerFilename, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The best candidate found so far (if any) is still safely at
                // trailerFilename, untouched - only this newer, better one couldn't be
                // put in place, so stop rather than lose track of what's actually kept.
                _logger.LogWarning("  > Could not put a better trailer attempt in place, stopping search here: {Error}", ex.Message);
                break;
            }

            success = true;
            bestHeight = attemptHeight;
            UnixPermissions.MatchTo(trailerFilename, localPath, _logger);

            if (bestHeight is not null && bestHeight.Value >= config.MinTrailerResolution)
            {
                _logger.LogInformation("  > Found a candidate at {Height}p, which meets the {Min}p target - keeping it.", bestHeight, config.MinTrailerResolution);
                break;
            }

            if (bestHeight is not null)
            {
                _logger.LogInformation("  > Found a candidate at {Height}p, still below the {Min}p target - keeping it for now, but still searching...", bestHeight, config.MinTrailerResolution);
                continue;
            }

            // Resolution can't be probed at all (no ffprobe) - nothing more to gain by
            // trying further sources, since there'd be no way to compare them anyway.
            break;
        }

        return (success, bestHeight);
    }

    /// <summary>
    /// After a search/download attempt aimed at replacing an existing under-resolution
    /// trailer: keeps the new file only if it actually turned out higher resolution
    /// than the one it's replacing - an attempt that failed outright, or that also
    /// landed on a low-quality fallback, restores the original untouched rather than
    /// trading one low-quality trailer for another. Shared between movies and series.
    /// </summary>
    /// <returns>Whether the new trailer was kept (a genuine upgrade).</returns>
    private bool ResolveTrailerUpgrade(
        bool downloadSuccess,
        int? newHeight,
        string trailerFilename,
        string existingTrailerPath,
        string upgradeBackupPath,
        int? existingHeight)
    {
        if (!downloadSuccess)
        {
            RestoreUpgradeBackup(trailerFilename, existingTrailerPath, upgradeBackupPath);
            _logger.LogInformation("  > Could not find a better trailer - kept the existing one.");
            return false;
        }

        if (newHeight is not null && (existingHeight is null || newHeight.Value > existingHeight.Value))
        {
            try
            {
                File.Delete(upgradeBackupPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal: the new (better) trailer is already saved at
                // trailerFilename, so the movie/series is left with a valid trailer
                // either way - a lingering ".upgrading" backup file is just clutter.
                _logger.LogWarning("  > Could not remove the old trailer backup {Path}: {Error}", upgradeBackupPath, ex.Message);
            }

            _logger.LogInformation("  > Upgraded trailer resolution: {Old}p -> {New}p.", existingHeight, newHeight);
            return true;
        }

        RestoreUpgradeBackup(trailerFilename, existingTrailerPath, upgradeBackupPath);
        _logger.LogInformation(
            "  > New attempt ({New}p) wasn't better than the existing trailer ({Old}p) - kept the existing one.",
            newHeight,
            existingHeight);
        return false;
    }

    private void RestoreUpgradeBackup(string trailerFilename, string existingTrailerPath, string upgradeBackupPath)
    {
        // Restore the original first, before cleaning up anything left under a
        // different name - otherwise, for that different-name case, there'd be a
        // moment where neither the original nor the rejected attempt exists under any
        // name Jellyfin recognizes, which its own file-watcher could race against (the
        // same class of issue DownloadBestAsync avoids for its own per-candidate
        // churn).
        try
        {
            File.Move(upgradeBackupPath, existingTrailerPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The backup is still sitting at upgradeBackupPath (Jellyfin's local-extras
            // resolver won't recognize its ".upgrading" name as a trailer, but the file
            // itself isn't lost) - worth an ERROR since this leaves the movie/series
            // without a *recognized* trailer until it's manually renamed back or the
            // next run's upgrade attempt succeeds.
            _logger.LogError(
                "  > Could not restore the existing trailer backup from {Backup} to {Path}: {Error}",
                upgradeBackupPath,
                existingTrailerPath,
                ex.Message);
            return;
        }

        // The rejected attempt may have saved under a different filename than the
        // original (e.g. a legacy "trailer.mp4" name vs. the current
        // "<Title>-trailer.mp4" convention) - clean it up separately, since the
        // restore above only replaced existingTrailerPath, not this unrelated stray
        // file. Non-fatal: the original is already safely restored either way, a
        // lingering stray file is just clutter.
        if (File.Exists(trailerFilename) && !string.Equals(trailerFilename, existingTrailerPath, StringComparison.Ordinal))
        {
            try
            {
                File.Delete(trailerFilename);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("  > Could not remove a superseded trailer attempt {Path}: {Error}", trailerFilename, ex.Message);
            }
        }
    }

    private enum ThemeSongOutcome
    {
        /// <summary>Theme song fetching is off in configuration; nothing was attempted.</summary>
        Disabled,

        /// <summary>A theme.mp3 already existed - untouched, whether user-provided or from a previous run.</summary>
        AlreadyHad,

        /// <summary>A theme song was found on ThemerrDB and (dry run aside) downloaded successfully.</summary>
        Downloaded,

        /// <summary>No TMDb id, no ThemerrDB entry for it, or the download itself failed.</summary>
        NotFound
    }

    /// <summary>
    /// Fetches a local theme song (<c>theme.mp3</c>) for a movie/series via ThemerrDB
    /// (see <see cref="ThemerrDbClient"/>), if enabled. Shared between movies and
    /// series - the logic has no movie/series-specific behavior beyond which ThemerrDB
    /// endpoint to query, unlike trailers, since there's no title/duration/keyword
    /// filtering to do here (ThemerrDB's own curators already did that).
    /// </summary>
    private async Task<ThemeSongOutcome> FetchThemeSongAsync(
        BaseItem item,
        string folderPath,
        bool isSeries,
        PluginConfiguration config,
        YtDlpClient ytDlp,
        ThemerrDbClient themerrDb,
        CancellationToken cancellationToken)
    {
        if (!config.FetchThemeSongs)
        {
            return ThemeSongOutcome.Disabled;
        }

        var themePath = Path.Combine(folderPath, "theme.mp3");
        if (File.Exists(themePath))
        {
            return ThemeSongOutcome.AlreadyHad;
        }

        if (!item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbId) || string.IsNullOrEmpty(tmdbId))
        {
            return ThemeSongOutcome.NotFound;
        }

        // Matches trailer fetching's own dry-run behavior: no network calls at all, so
        // a dry run stays free of side effects and instant.
        if (config.DryRun)
        {
            _logger.LogInformation("  > [DRY-RUN] Would look up and fetch a theme song from ThemerrDB if available.");
            return ThemeSongOutcome.Downloaded;
        }

        var themeUrl = isSeries
            ? await themerrDb.GetSeriesThemeUrlAsync(tmdbId, cancellationToken).ConfigureAwait(false)
            : await themerrDb.GetMovieThemeUrlAsync(tmdbId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(themeUrl))
        {
            return ThemeSongOutcome.NotFound;
        }

        _logger.LogInformation("  > Fetching theme song from ThemerrDB ({Url})...", themeUrl);
        var success = await ytDlp.DownloadAudioAsync(themeUrl, themePath, cancellationToken).ConfigureAwait(false);
        if (success && !string.IsNullOrEmpty(item.Path))
        {
            // item.Path is the movie's own file for a movie, or the series' own
            // folder for a series - either way, an already-correctly-permissioned
            // reference for whatever this specific library item's setup actually is.
            UnixPermissions.MatchTo(themePath, item.Path, _logger);
        }

        return success ? ThemeSongOutcome.Downloaded : ThemeSongOutcome.NotFound;
    }

    private static void ApplyThemeSongOutcome(ThemeSongOutcome outcome, TrailerFetchStats stats, bool isSeries)
    {
        switch (outcome)
        {
            case ThemeSongOutcome.AlreadyHad:
                if (isSeries)
                {
                    stats.SeriesThemeSongAlreadyHad++;
                }
                else
                {
                    stats.ThemeSongAlreadyHad++;
                }

                break;
            case ThemeSongOutcome.Downloaded:
                if (isSeries)
                {
                    stats.SeriesThemeSongDownloaded++;
                }
                else
                {
                    stats.ThemeSongDownloaded++;
                }

                break;
            case ThemeSongOutcome.NotFound:
                if (isSeries)
                {
                    stats.SeriesThemeSongNotFound++;
                }
                else
                {
                    stats.ThemeSongNotFound++;
                }

                break;
            case ThemeSongOutcome.Disabled:
                break;
        }
    }

    /// <summary>
    /// Processes a single TV series: tries its official RemoteTrailers, then a
    /// multi-stage YouTube search (see <see cref="TrailerSources"/>), same duration/
    /// title filtering as movies. Title/year resolution and source-query building are
    /// shared with ProcessMovieAsync via <see cref="ItemMetadata"/>/
    /// <see cref="TrailerSources"/> (both are plain BaseItem lookups, no movie- or
    /// series-specific behavior); kept as a separate method rather than a shared "item"
    /// loop because the actual steps genuinely differ - no rename/migrate here, since a
    /// series always already lives in its own dedicated folder, so the "own folder"
    /// problem that drives that logic for movies (jellyfin/jellyfin#10077) doesn't
    /// apply, and validity is a folder-exists check rather than
    /// <see cref="MovieFileOperations.IsValidMediaFile"/>.
    /// </summary>
    private async Task ProcessSeriesAsync(Series series, PluginConfiguration config, YtDlpClient ytDlp, ThemerrDbClient themerrDb, TrailerFetchStats stats, string? ffprobePath, CancellationToken cancellationToken)
    {
        var rawTitle = string.IsNullOrEmpty(series.Name) ? "Unknown" : series.Name;
        var seriesPath = series.Path;

        if (string.IsNullOrEmpty(seriesPath) || !Directory.Exists(seriesPath))
        {
            _logger.LogWarning("Skipping series {Title}: folder not found ({Path})", rawTitle, seriesPath);
            stats.SeriesSkipped++;
            return;
        }

        var libraryRoot = series.GetTopParent()?.Path;
        stats.SeriesScanned++;

        _logger.LogInformation("*** Processing series: {Name}", PathDisplay.Relative(seriesPath, libraryRoot));

        var (resolvedTitle, resolvedVariants) = ItemMetadata.ResolveTitles(series, seriesPath);

        // Season-range noise ("S1", "S1 - S5") is TV-specific - see
        // SeriesTitleCleanup for why this is a separate step rather than something
        // ItemMetadata/TitleMatching (shared with movies) needs to know about.
        var preferredTitle = SeriesTitleCleanup.StripSeasonRange(resolvedTitle);
        var titleVariants = resolvedVariants
            .Select(SeriesTitleCleanup.StripSeasonRange)
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

        var year = ItemMetadata.ResolveYear(series, seriesPath);
        var yearStr = year is not null ? $" ({year})" : string.Empty;
        var safeTitle = TitleMatching.SanitizeFilename($"{preferredTitle}{yearStr}");

        _logger.LogInformation("  > using title {Title}", preferredTitle);

        if (config.RenameSeriesFolders)
        {
            // Captured before any renaming below - Jellyfin's own cached Season.Path
            // would go stale the moment the series' top-level folder is renamed
            // (Directory.Move happens purely on disk, Jellyfin doesn't know until its
            // next library scan), but a season subfolder's own name never changes from
            // a top-level rename, so it's still valid to reuse once combined with
            // whatever the series' new path ends up being.
            var seasonFolderNames = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Season },
                Recursive = false,
                IsVirtualItem = false,
                Parent = series
            })
                .OfType<Season>()
                .Where(s => !string.IsNullOrEmpty(s.Path))
                .Select(s => (FolderName: Path.GetFileName(s.Path), s.IndexNumber))
                .ToList();

            var (newSeriesPath, renamed) = SeriesFileOperations.RenameSeriesFolder(seriesPath, safeTitle, config.DryRun, libraryRoot, _logger);
            if (renamed)
            {
                stats.SeriesRenamed++;
                seriesPath = newSeriesPath;
            }

            foreach (var (folderName, indexNumber) in seasonFolderNames)
            {
                var seasonPath = Path.Combine(seriesPath, folderName);
                if (SeriesFileOperations.RenameSeasonFolder(seasonPath, indexNumber, config.DryRun, libraryRoot, _logger))
                {
                    stats.SeriesSeasonsRenamed++;
                }
            }
        }

        // Confirmed live: an existing series folder's permission bits/group can deny
        // this plugin's own process write access (same class of issue as a movie's
        // folder - see UnixPermissions), but unlike the movie theme-song folder
        // (healed every run below, before writing) nothing previously healed a
        // series folder outside of the one-time RenameSeriesFolder migration above -
        // so a pre-existing series folder's permission drift was never fixed before
        // either a trailer or theme song write into it. Using an existing episode
        // file as the reference, since a series has no single item.Path of its own.
        var referenceEpisode = SeriesFileOperations.FindReferenceEpisode(seriesPath, _logger);
        if (referenceEpisode is not null)
        {
            UnixPermissions.MatchTo(seriesPath, referenceEpisode, _logger);
        }

        var trailerFilename = Path.Combine(seriesPath, $"{safeTitle}-trailer.mp4");

        var trailerCandidates = new[]
        {
            trailerFilename,
            Path.Combine(seriesPath, $"{safeTitle}-trailer.mkv"),
            Path.Combine(seriesPath, "trailer.mp4"),
            Path.Combine(seriesPath, "trailer.mkv")
        };
        var existingTrailerPath = trailerCandidates.FirstOrDefault(File.Exists);
        var alreadyHadTrailer = series.LocalTrailers.Count > 0 || existingTrailerPath is not null;
        var downloadSuccess = false;

        if (alreadyHadTrailer)
        {
            stats.SeriesAlreadyHadTrailer++;
        }

        var (shouldSearch, upgradeBackupPath, existingHeight) = await PrepareUpgradeAttemptAsync(
            alreadyHadTrailer, existingTrailerPath, config, ffprobePath, cancellationToken).ConfigureAwait(false);

        if (alreadyHadTrailer && !shouldSearch)
        {
            _logger.LogInformation("  > Trailer already exists, skipping.");
            var earlyThemeOutcome = await FetchThemeSongAsync(series, seriesPath, isSeries: true, config, ytDlp, themerrDb, cancellationToken).ConfigureAwait(false);
            ApplyThemeSongOutcome(earlyThemeOutcome, stats, isSeries: true);
            return;
        }

        // Resolution/audio preference are honored on every search, not just an
        // upgrade re-check (see DownloadBestAsync) - so a fresh item doesn't need a
        // later "Update existing trailers" run just to reach quality it could have
        // gotten immediately.
        var sourcesToTry = TrailerSources.Build(series, titleVariants, year, skipNativeLanguage: config.AllowUpgradeInOtherLanguage);

        // No single "runtime" to compare a series trailer against, unlike a movie -
        // only the universal duration cap applies.
        var (bestSuccess, bestHeight) = await DownloadBestAsync(
            sourcesToTry, trailerFilename, titleVariants, itemDurationSeconds: null, seriesPath, config, ffprobePath, ytDlp, cancellationToken).ConfigureAwait(false);
        downloadSuccess = bestSuccess;

        if (upgradeBackupPath is not null)
        {
            if (ResolveTrailerUpgrade(downloadSuccess, bestHeight, trailerFilename, existingTrailerPath!, upgradeBackupPath, existingHeight))
            {
                stats.SeriesUpgraded++;
                UnixPermissions.MatchTo(trailerFilename, seriesPath, _logger);
            }
        }
        else if (downloadSuccess)
        {
            stats.SeriesDownloaded++;
        }
        else
        {
            stats.SeriesNotFound++;
        }

        var themeOutcome = await FetchThemeSongAsync(series, seriesPath, isSeries: true, config, ytDlp, themerrDb, cancellationToken).ConfigureAwait(false);
        ApplyThemeSongOutcome(themeOutcome, stats, isSeries: true);
    }

    private void LogSummary(TrailerFetchStats stats, int totalMovies, int totalSeries, bool dryRun, DateTime startedAt, string? stopReason)
    {
        var completedAt = DateTime.UtcNow;
        RunSummaryStore.Save(
            Plugin.Instance!.DataFolderPath,
            new RunSummary(
                completedAt,
                (completedAt - startedAt).TotalSeconds,
                stopReason,
                dryRun,
                totalMovies,
                stats.Scanned,
                stats.AlreadyHadTrailer,
                stats.Downloaded,
                stats.NotFound,
                stats.Skipped,
                stats.Renamed,
                stats.Migrated,
                totalSeries,
                stats.SeriesScanned,
                stats.SeriesAlreadyHadTrailer,
                stats.SeriesDownloaded,
                stats.SeriesNotFound,
                stats.SeriesSkipped,
                stats.MoviePhaseStarted,
                stats.SeriesPhaseStarted,
                stats.Upgraded,
                stats.SeriesUpgraded,
                stats.ThemeSongAlreadyHad,
                stats.ThemeSongDownloaded,
                stats.ThemeSongNotFound,
                stats.SeriesThemeSongAlreadyHad,
                stats.SeriesThemeSongDownloaded,
                stats.SeriesThemeSongNotFound,
                stats.SeriesRenamed,
                stats.SeriesSeasonsRenamed));

        // "0" and "never got to it" look identical as a bare count otherwise - e.g. a
        // run that got rate-limited partway through movies, with series never
        // touched, would show "Series Processed: 0" exactly like a run that reached
        // every series and matched none.
        static object Fmt(bool phaseStarted, int count) => phaseStarted ? count : "n/a";

        _logger.LogInformation(string.Empty);
        _logger.LogInformation("==========================================");
        if (stopReason is not null)
        {
            _logger.LogInformation("     TRAILER SYNC SUMMARY [{StopReason}]   ", stopReason);
        }
        else
        {
            _logger.LogInformation("           TRAILER SYNC SUMMARY           ");
        }

        _logger.LogInformation("==========================================");

        // Only the libraries actually in scope for this run get a section - a run
        // scoped to series-only libraries showing "Total Movies in Library: 0" reads
        // as if something's wrong rather than as "no movies were in scope".
        if (totalMovies > 0)
        {
            _logger.LogInformation("  Total Movies in Library : {Count}", totalMovies);
            _logger.LogInformation("  Movies Processed        : {Count}", Fmt(stats.MoviePhaseStarted, stats.Scanned));
            _logger.LogInformation("  Already had Trailer     : {Count}", Fmt(stats.MoviePhaseStarted, stats.AlreadyHadTrailer));
            _logger.LogInformation(dryRun ? "  Trailers Found (Dry-Run): {Count}" : "  Trailers Downloaded     : {Count}", Fmt(stats.MoviePhaseStarted, stats.Downloaded));
            _logger.LogInformation("  No Trailer Found        : {Count}", Fmt(stats.MoviePhaseStarted, stats.NotFound));
            if (stats.Skipped > 0)
            {
                _logger.LogInformation("  Skipped (Unreachable)   : {Count}", stats.Skipped);
            }

            if (stats.Renamed > 0)
            {
                _logger.LogInformation("  Original Files Renamed  : {Count}", stats.Renamed);
            }

            if (stats.Migrated > 0)
            {
                _logger.LogInformation("  Migrated to Own Folder  : {Count}", stats.Migrated);
            }

            if (stats.Upgraded > 0)
            {
                _logger.LogInformation("  Trailers Upgraded       : {Count}", stats.Upgraded);
            }

            if (stats.ThemeSongAlreadyHad + stats.ThemeSongDownloaded + stats.ThemeSongNotFound > 0)
            {
                _logger.LogInformation("  Theme Songs (had/new/not found): {AlreadyHad}/{Downloaded}/{NotFound}", stats.ThemeSongAlreadyHad, stats.ThemeSongDownloaded, stats.ThemeSongNotFound);
            }
        }

        if (totalSeries > 0)
        {
            if (totalMovies > 0)
            {
                _logger.LogInformation("  ---------------- TV Series --------------");
            }

            _logger.LogInformation("  Total Series in Library : {Count}", totalSeries);
            _logger.LogInformation("  Series Processed        : {Count}", Fmt(stats.SeriesPhaseStarted, stats.SeriesScanned));
            _logger.LogInformation("  Already had Trailer     : {Count}", Fmt(stats.SeriesPhaseStarted, stats.SeriesAlreadyHadTrailer));
            _logger.LogInformation(dryRun ? "  Trailers Found (Dry-Run): {Count}" : "  Trailers Downloaded     : {Count}", Fmt(stats.SeriesPhaseStarted, stats.SeriesDownloaded));
            _logger.LogInformation("  No Trailer Found        : {Count}", Fmt(stats.SeriesPhaseStarted, stats.SeriesNotFound));
            if (stats.SeriesSkipped > 0)
            {
                _logger.LogInformation("  Skipped (No Folder)     : {Count}", stats.SeriesSkipped);
            }

            if (stats.SeriesRenamed > 0)
            {
                _logger.LogInformation("  Series Folders Renamed  : {Count}", stats.SeriesRenamed);
            }

            if (stats.SeriesSeasonsRenamed > 0)
            {
                _logger.LogInformation("  Season Folders Renamed  : {Count}", stats.SeriesSeasonsRenamed);
            }

            if (stats.SeriesUpgraded > 0)
            {
                _logger.LogInformation("  Trailers Upgraded       : {Count}", stats.SeriesUpgraded);
            }

            if (stats.SeriesThemeSongAlreadyHad + stats.SeriesThemeSongDownloaded + stats.SeriesThemeSongNotFound > 0)
            {
                _logger.LogInformation("  Theme Songs (had/new/not found): {AlreadyHad}/{Downloaded}/{NotFound}", stats.SeriesThemeSongAlreadyHad, stats.SeriesThemeSongDownloaded, stats.SeriesThemeSongNotFound);
            }
        }

        if (totalMovies == 0 && totalSeries == 0)
        {
            _logger.LogInformation("  Nothing in scope for this run.");
        }

        _logger.LogInformation("==========================================");
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        ];
    }

    private sealed class TrailerFetchStats
    {
        public string? LastDir { get; set; }

        public int Scanned { get; set; }

        public int AlreadyHadTrailer { get; set; }

        public int Downloaded { get; set; }

        public int NotFound { get; set; }

        public int Skipped { get; set; }

        public int Renamed { get; set; }

        public int Migrated { get; set; }

        public int Upgraded { get; set; }

        public int ThemeSongAlreadyHad { get; set; }

        public int ThemeSongDownloaded { get; set; }

        public int ThemeSongNotFound { get; set; }

        public int SeriesScanned { get; set; }

        public int SeriesAlreadyHadTrailer { get; set; }

        public int SeriesDownloaded { get; set; }

        public int SeriesNotFound { get; set; }

        public int SeriesSkipped { get; set; }

        public int SeriesRenamed { get; set; }

        public int SeriesSeasonsRenamed { get; set; }

        public int SeriesUpgraded { get; set; }

        public int SeriesThemeSongAlreadyHad { get; set; }

        public int SeriesThemeSongDownloaded { get; set; }

        public int SeriesThemeSongNotFound { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the movie loop was ever entered
        /// this run - false only if the run stopped (cancelled/rate-limited) before
        /// reaching it at all, which the movie counts above can't distinguish from
        /// "genuinely processed zero" on their own.
        /// </summary>
        public bool MoviePhaseStarted { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the series loop was ever entered
        /// this run - false if the run stopped while still working through movies
        /// (movies are always processed first), which otherwise looks identical to
        /// "128 series in scope, 0 matched" in the summary.
        /// </summary>
        public bool SeriesPhaseStarted { get; set; }
    }
}
