using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Enricherr.ScheduledTasks;
using Jellyfin.Plugin.Enricherr.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.Api;

/// <summary>
/// A library the admin can pick to scope scanning to, as shown on the settings page.
/// </summary>
/// <param name="Id">The library's ItemId (a Guid, as a string).</param>
/// <param name="Name">The library's display name.</param>
/// <param name="CollectionType">The library's configured content type (e.g. "movies"), if any.</param>
/// <remarks>
/// Property names are pinned explicitly to camelCase via <see cref="JsonPropertyNameAttribute"/>
/// rather than relying on Jellyfin's host-level JSON casing configuration (which serializes
/// most of its own API PascalCase), so the settings page's JS can rely on a fixed casing
/// regardless of how that global option is set.
/// </remarks>
public record LibraryInfoDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("collectionType")] string? CollectionType);

/// <summary>
/// One row of the settings page's "Overall" statistics table: a single Jellyfin
/// library (movies and series both counted in, if it's a mixed-content library),
/// with how many of its items currently have a local trailer/theme song right now -
/// not just what the last run happened to find or download.
/// </summary>
/// <param name="Name">The library's display name.</param>
/// <param name="Items">Total movies + series in this library.</param>
/// <param name="Trailers">How many of those currently have a local trailer, per Jellyfin's own LocalTrailers.</param>
/// <param name="ThemeSongs">How many of those, in their own dedicated folder, currently have a local theme.mp3.</param>
public record LibraryTotalsRow(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("items")] int Items,
    [property: JsonPropertyName("trailers")] int Trailers,
    [property: JsonPropertyName("themeSongs")] int ThemeSongs);

/// <summary>One entry (file or directory) in the settings page's debug file browser.</summary>
/// <param name="Name">The entry's own display name (just the last path segment).</param>
/// <param name="Path">The entry's full filesystem path.</param>
/// <param name="IsDirectory">Whether this entry can be descended into.</param>
public record FileSystemEntryDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("isDirectory")] bool IsDirectory);

/// <summary>One directory listing from the settings page's debug file browser.</summary>
/// <param name="Path">The directory actually listed, or null for the top-level "pick a library" listing.</param>
/// <param name="ParentPath">The path to go up one level to, or null if this is already a library root.</param>
/// <param name="Entries">Subdirectories first, then video files - both alphabetical.</param>
public record BrowseFileSystemResult(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("parentPath")] string? ParentPath,
    [property: JsonPropertyName("entries")] List<FileSystemEntryDto> Entries);

/// <summary>Request body for <see cref="EnricherrController.RunSingleItem"/>.</summary>
/// <param name="Path">Full filesystem path to the movie file to run this plugin's own processing against.</param>
public record RunSingleItemRequest([property: JsonPropertyName("path")] string Path);

/// <summary>
/// Handles uploading/removing the yt-dlp cookies file, and listing libraries, from the
/// plugin's settings page. A headless server has no browser profile to read cookies
/// from directly (unlike the standalone script's --cookie-browser), so authenticated/
/// age-restricted YouTube access instead relies on an exported Netscape-format
/// cookies.txt uploaded here.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Enricherr")]
public class EnricherrController : ControllerBase
{
    private const string CookiesFileName = "cookies.txt";
    private const long MaxCookiesFileBytes = 2 * 1024 * 1024; // 2 MB is generous for a cookie jar

    private readonly ILogger<EnricherrController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly FetchTrailersTask _fetchTrailersTask;
    private readonly LibraryItemsFinder _libraryItemsFinder;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnricherrController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{EnricherrController}"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface, used to confirm a run is actually still active before trusting its live-progress snapshot.</param>
    /// <param name="fetchTrailersTask">Instance of the <see cref="FetchTrailersTask"/> class, used for the debug file picker's single-item runs.</param>
    public EnricherrController(ILogger<EnricherrController> logger, ILibraryManager libraryManager, ITaskManager taskManager, FetchTrailersTask fetchTrailersTask)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _fetchTrailersTask = fetchTrailersTask;
        _libraryItemsFinder = new LibraryItemsFinder(libraryManager, logger);
    }

    /// <summary>
    /// Lists the server's libraries, for the settings page to offer as scan-scope choices.
    /// </summary>
    /// <returns>The list of libraries.</returns>
    [HttpGet("Libraries")]
    public ActionResult<IEnumerable<LibraryInfoDto>> GetLibraries()
    {
        var libraries = _libraryManager.GetVirtualFolders()
            .Select(f => new LibraryInfoDto(f.ItemId, f.Name, f.CollectionType?.ToString()))
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(libraries);
    }

    /// <summary>
    /// Returns one row per Jellyfin library on the server - always every library,
    /// regardless of which ones the plugin is currently configured to scan, since
    /// this is meant to show overall server coverage rather than just current scan
    /// scope - with how many of its movies/series currently have a local trailer/
    /// theme song right now. A read-only, on-demand count against Jellyfin's own
    /// already-loaded metadata (LocalTrailers) plus a plain file-existence check for
    /// theme.mp3 (Jellyfin has no first-class "theme song" tracking to query
    /// instead) - not cached, computed fresh each time the settings page asks.
    /// </summary>
    /// <returns>One row per library.</returns>
    [HttpGet("LibraryTotals")]
    public ActionResult<IEnumerable<LibraryTotalsRow>> GetLibraryTotals()
    {
        var rows = _libraryManager.GetVirtualFolders()
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                var libraryIds = new[] { f.ItemId };
                var movies = _libraryItemsFinder.GetMovies(libraryIds, logProgress: false);
                var series = _libraryItemsFinder.GetSeries(libraryIds, logProgress: false);

                var trailers = movies.Count(m => m.LocalTrailers.Count > 0) + series.Count(s => s.LocalTrailers.Count > 0);
                var themeSongs =
                    movies.Count(m =>
                        !string.IsNullOrEmpty(m.Path) &&
                        MovieFileOperations.HasOwnFolder(m.Path) &&
                        System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(m.Path)!, "theme.mp3"))) +
                    series.Count(s => !string.IsNullOrEmpty(s.Path) && System.IO.File.Exists(Path.Combine(s.Path, "theme.mp3")));

                return new LibraryTotalsRow(f.Name, movies.Count + series.Count, trailers, themeSongs);
            })
            .ToList();

        return Ok(rows);
    }

    /// <summary>
    /// Returns the outcome of the most recent "Fetch Missing Trailers" run, for the
    /// settings page to display without digging through the log.
    /// </summary>
    /// <returns>The last run's summary, or 204 if the task hasn't run yet.</returns>
    [HttpGet("LastRunSummary")]
    public ActionResult<RunSummary> GetLastRunSummary()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not available.");
        var summary = RunSummaryStore.Load(plugin.DataFolderPath);
        return summary is null ? NoContent() : Ok(summary);
    }

    /// <summary>
    /// Returns a live snapshot of the "Fetch Missing Trailers" task while it's
    /// actually running, for the settings page to poll instead of only seeing the
    /// final result once the whole run completes. Gated on Jellyfin's own
    /// <see cref="ITaskManager"/> reporting the task as genuinely
    /// <see cref="TaskState.Running"/> - not just on whether a snapshot file exists -
    /// so a leftover snapshot from a run that crashed or was killed abnormally
    /// (never reaching the normal end-of-run cleanup) is never mistaken for a live one.
    /// </summary>
    /// <returns>The current snapshot, or 204 if no run is actively in progress.</returns>
    [HttpGet("LiveProgress")]
    public ActionResult<LiveProgress> GetLiveProgress()
    {
        var isRunning = _taskManager.ScheduledTasks
            .Any(worker => worker.ScheduledTask.Key == "FetchMissingTrailers" && worker.State == TaskState.Running);
        if (!isRunning)
        {
            return NoContent();
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not available.");
        var progress = LiveProgressStore.Load(plugin.DataFolderPath);
        return progress is null ? NoContent() : Ok(progress);
    }

    /// <summary>
    /// Lists a directory for the settings page's debug file browser - deliberately
    /// scoped to only ever descend into a configured Jellyfin library's own root
    /// folder(s), the same set <see cref="ILibraryManager.GetVirtualFolders()"/> itself
    /// reports, rather than browsing the server's whole filesystem: Jellyfin (often
    /// containerized) typically has access to exactly those paths anyway, and this
    /// keeps the picker showing only places a movie file could plausibly live.
    /// </summary>
    /// <param name="path">The directory to list, or omit/empty for the top-level list of library roots.</param>
    /// <returns>The directory's contents, or the library-root list.</returns>
    [HttpGet("BrowseFileSystem")]
    public ActionResult<BrowseFileSystemResult> BrowseFileSystem([FromQuery] string? path)
    {
        var libraryRoots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations.Select(loc => (Library: f.Name, Root: loc)))
            .ToList();

        if (string.IsNullOrEmpty(path))
        {
            var multiLocationLibraries = libraryRoots.GroupBy(r => r.Library).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            var rootEntries = libraryRoots
                .Select(r => new FileSystemEntryDto(
                    multiLocationLibraries.Contains(r.Library) ? $"{r.Library} ({Path.GetFileName(r.Root.TrimEnd(Path.DirectorySeparatorChar))})" : r.Library,
                    r.Root,
                    true))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Ok(new BrowseFileSystemResult(null, null, rootEntries));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BadRequest("Invalid path.");
        }

        if (!libraryRoots.Any(r => IsPathUnderRoot(fullPath, r.Root)))
        {
            return BadRequest("Path is not inside any configured library.");
        }

        if (!Directory.Exists(fullPath))
        {
            return NotFound("Directory does not exist.");
        }

        List<FileSystemEntryDto> entries;
        try
        {
            var directories = Directory.GetDirectories(fullPath)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => new FileSystemEntryDto(Path.GetFileName(d), d, true));
            var files = Directory.GetFiles(fullPath)
                .Where(MovieFileOperations.HasVideoExtension)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileSystemEntryDto(Path.GetFileName(f), f, false));
            entries = directories.Concat(files).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Could not list directory: {e.Message}");
        }

        var isLibraryRoot = libraryRoots.Any(r => IsPathUnderRoot(fullPath, r.Root) && IsPathUnderRoot(r.Root, fullPath));
        var parentPath = isLibraryRoot ? null : Path.GetDirectoryName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
        return Ok(new BrowseFileSystemResult(fullPath, parentPath, entries));
    }

    /// <summary>
    /// Runs this plugin's own per-movie processing (title resolution, missing-
    /// metadata search if enabled, trailer/theme song fetch, rename/migrate/subtitle
    /// rename) against exactly one movie the debug file picker selected, without
    /// touching or waiting on a full library scan.
    /// </summary>
    /// <param name="request">The movie file's full path, or its own dedicated folder's path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What happened for this one item.</returns>
    [HttpPost("RunSingleItem")]
    public async Task<ActionResult<FetchTrailersTask.SingleItemRunResult>> RunSingleItem([FromBody] RunSingleItemRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Path))
        {
            return BadRequest("No path provided.");
        }

        var filePath = request.Path;

        // A folder was selected (e.g. to exercise RenameLooseSubtitles, which only
        // matters once a movie already lives in its own dedicated folder) rather
        // than the movie file itself - Jellyfin has no BaseItem for the folder
        // itself in this layout (its Movie item's own Path is the video file), so
        // resolve it ourselves: the one video file directly inside it, if there is
        // exactly one.
        if (Directory.Exists(filePath) && !System.IO.File.Exists(filePath))
        {
            List<string> videoFiles;
            try
            {
                videoFiles = Directory.GetFiles(filePath).Where(MovieFileOperations.HasVideoExtension).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Could not list folder: {e.Message}");
            }

            if (videoFiles.Count == 0)
            {
                return NotFound("No video file found directly in this folder.");
            }

            if (videoFiles.Count > 1)
            {
                return BadRequest("This folder has more than one video file - pick the specific file to run against instead.");
            }

            filePath = videoFiles[0];
        }

        var item = _libraryManager.FindByPath(filePath, isFolder: false);
        if (item is not Movie movie)
        {
            return NotFound("This isn't a movie Jellyfin knows about yet - scan the library first (Dashboard -> Libraries -> Scan), then try again.");
        }

        var result = await _fetchTrailersTask.RunForSingleItemAsync(movie, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Uploads a Netscape-format cookies.txt file, storing it in the plugin's data
    /// folder and pointing the configuration at it.
    /// </summary>
    /// <param name="file">The uploaded cookies.txt file.</param>
    /// <returns>The path the file was saved to.</returns>
    [HttpPost("Cookies")]
    [RequestSizeLimit(MaxCookiesFileBytes)]
    public async Task<ActionResult<string>> UploadCookies(IFormFile file)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest("No file was uploaded.");
        }

        if (file.Length > MaxCookiesFileBytes)
        {
            return BadRequest("Cookies file is too large.");
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not available.");
        Directory.CreateDirectory(plugin.DataFolderPath);
        var destinationPath = Path.Combine(plugin.DataFolderPath, CookiesFileName);

        await using (var stream = System.IO.File.Create(destinationPath))
        {
            await file.CopyToAsync(stream).ConfigureAwait(false);
        }

        plugin.Configuration.CookiesFilePath = destinationPath;
        plugin.SaveConfiguration();

        _logger.LogInformation("Cookies file uploaded to {Path} ({Bytes} bytes).", destinationPath, file.Length);
        return Ok(destinationPath);
    }

    /// <summary>
    /// Removes the currently configured cookies file, if any, and clears the setting.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpDelete("Cookies")]
    public ActionResult RemoveCookies()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not available.");
        var path = plugin.Configuration.CookiesFilePath;

        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }

        plugin.Configuration.CookiesFilePath = string.Empty;
        plugin.SaveConfiguration();

        _logger.LogInformation("Cookies file removed.");
        return NoContent();
    }
}
