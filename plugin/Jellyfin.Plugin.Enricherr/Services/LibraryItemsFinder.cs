using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Resolves the movies/series in scope for a configured set of library ids - shared
/// between <see cref="ScheduledTasks.FetchTrailersTask"/> (an actual run) and the
/// settings page's library-totals display (a lightweight, read-only count), so both
/// use the exact same "which items are in scope" logic rather than two copies that
/// could drift out of sync.
/// </summary>
public class LibraryItemsFinder
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryItemsFinder"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public LibraryItemsFinder(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Resolves which libraries are in scope for a run - the specifically configured
    /// ones, or every library on the server if none were configured - each already
    /// resolved to its own root <see cref="BaseItem"/> rather than just an id, since
    /// a scoped scan (<see cref="Folder.ValidateChildren(IProgress{double}, System.Threading.CancellationToken)"/>) needs the actual item to
    /// call it on, not just its id string.
    /// </summary>
    /// <param name="libraryIds">Configured library (VirtualFolder) ids, or empty for every library.</param>
    public List<BaseItem> ResolveLibrariesInScope(string[] libraryIds)
    {
        // Alphabetical regardless of which branch resolves them - the settings
        // page's "Overall" tab (and the live-progress table computed from this same
        // order) always list libraries alphabetically, so a run scoped to a specific,
        // *configured* set of libraries (this branch) needs to process them in that
        // same order too, or the live progress table's top-to-bottom flow wouldn't
        // match which library is actually the spinner's next stop.
        if (libraryIds.Length > 0)
        {
            return ResolveLibraries(libraryIds, logProgress: true)
                .OrderBy(lib => lib.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var libraries = new List<BaseItem>();
        foreach (var folderInfo in _libraryManager.GetVirtualFolders().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(folderInfo.ItemId, out var guid))
            {
                _logger.LogWarning("Library {Name} has an unparseable id {Id}, skipping it.", folderInfo.Name, folderInfo.ItemId);
                continue;
            }

            var libraryItem = _libraryManager.GetItemById(guid);
            if (libraryItem is null)
            {
                _logger.LogWarning("Library {Name} ({Id}) did not resolve to any item, skipping it.", folderInfo.Name, folderInfo.ItemId);
                continue;
            }

            libraries.Add(libraryItem);
        }

        return libraries;
    }

    /// <summary>Movies in scope: every library if <paramref name="libraryIds"/> is empty, otherwise just those.</summary>
    /// <param name="libraryIds">Configured library (VirtualFolder) ids, or empty for every library.</param>
    /// <param name="logProgress">Whether to log per-library progress - on for an actual run, off for a quick read-only count.</param>
    public List<Movie> GetMovies(string[] libraryIds, bool logProgress = true)
    {
        if (libraryIds.Length == 0)
        {
            if (logProgress)
            {
                _logger.LogInformation("No specific libraries selected - scanning all libraries.");
            }

            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false
            }).OfType<Movie>().ToList();
        }

        var movies = new List<Movie>();
        var seenIds = new HashSet<Guid>();

        foreach (var libraryItem in ResolveLibraries(libraryIds, logProgress))
        {
            var libraryMovies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false,
                Parent = libraryItem
            });

            foreach (var movie in libraryMovies.OfType<Movie>())
            {
                if (seenIds.Add(movie.Id))
                {
                    movies.Add(movie);
                }
            }
        }

        return movies;
    }

    /// <summary>TV series in scope: every library if <paramref name="libraryIds"/> is empty, otherwise just those.</summary>
    /// <param name="libraryIds">Configured library (VirtualFolder) ids, or empty for every library.</param>
    /// <param name="logProgress">Whether to log per-library progress - on for an actual run, off for a quick read-only count.</param>
    public List<Series> GetSeries(string[] libraryIds, bool logProgress = true)
    {
        if (libraryIds.Length == 0)
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false
            }).OfType<Series>().ToList();
        }

        var series = new List<Series>();
        var seenIds = new HashSet<Guid>();

        foreach (var libraryItem in ResolveLibraries(libraryIds, logProgress: false))
        {
            var librarySeries = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false,
                Parent = libraryItem
            });

            foreach (var s in librarySeries.OfType<Series>())
            {
                if (seenIds.Add(s.Id))
                {
                    series.Add(s);
                }
            }
        }

        return series;
    }

    /// <summary>
    /// Resolves each configured library id to its actual <see cref="BaseItem"/> via
    /// <see cref="ILibraryManager.GetItemById(Guid)"/> and queried individually via
    /// <see cref="InternalItemsQuery.Parent"/>, rather than hand-building
    /// <see cref="InternalItemsQuery.TopParentIds"/> from <c>VirtualFolderInfo.ItemId</c>
    /// directly - that id does not reliably match what TopParentIds-based filtering
    /// expects (confirmed: a query scoped this way returned 0 results against a
    /// library server-side reporting had 1026+ movies total), and TopParentIds is
    /// normally computed internally by LibraryManager from a resolved parent item, not
    /// meant to be built by callers from a raw id string.
    /// </summary>
    private IEnumerable<BaseItem> ResolveLibraries(string[] libraryIds, bool logProgress)
    {
        foreach (var id in libraryIds)
        {
            if (!Guid.TryParse(id, out var guid))
            {
                if (logProgress)
                {
                    _logger.LogWarning("Configured library id {Id} is not a valid GUID, skipping it.", id);
                }

                continue;
            }

            var libraryItem = _libraryManager.GetItemById(guid);
            if (libraryItem is null)
            {
                if (logProgress)
                {
                    _logger.LogWarning("Configured library id {Id} did not resolve to any item, skipping it.", guid);
                }

                continue;
            }

            if (logProgress)
            {
                _logger.LogInformation("Scanning library {Name}...", libraryItem.Name);
            }

            yield return libraryItem;
        }
    }
}
