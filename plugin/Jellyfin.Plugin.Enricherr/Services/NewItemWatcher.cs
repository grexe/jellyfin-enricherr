using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// Watches for newly-added movies/series and, once each one's OWN metadata refresh
/// has finished - not just the moment the library scanner first discovers the file,
/// whose title/year/ProviderIds aren't resolved yet at that point - triggers this
/// plugin's own scheduled task automatically, instead of only ever running on its
/// configured schedule or a manual click. Gated behind
/// <see cref="Configuration.PluginConfiguration.TriggerOnNewItem"/>, off by default;
/// the event subscriptions themselves stay active regardless (cheap to hold), so
/// toggling the setting takes effect immediately without needing a server restart.
///
/// Correlates two separate Jellyfin events to get this timing right:
/// <see cref="ILibraryManager.ItemAdded"/> fires the moment the library scanner (its
/// own filesystem watcher, or a scan) first discovers a file - metadata hasn't been
/// fetched yet at that point. <see cref="IProviderManager.RefreshCompleted"/> fires
/// once ANY item's metadata refresh finishes - not just a newly discovered item's
/// first refresh, but also every later routine refresh of an item already in the
/// library (Jellyfin re-refreshes existing items periodically on its own). Only a
/// RefreshCompleted for an item this watcher itself saw via ItemAdded (and hasn't
/// already handled) is treated as "a new item is ready" - otherwise a routine,
/// unrelated refresh of some other, already-known item would spuriously trigger a
/// full run too.
///
/// Debounced: several movies added in a burst (e.g. a bulk import) would otherwise
/// each independently trigger their own overlapping run - a single timer is instead
/// (re)started on every qualifying item, so the actual trigger only fires once
/// nothing new has shown up for <see cref="DebounceSeconds"/> seconds.
/// </summary>
public sealed class NewItemWatcher : IHostedService, IDisposable
{
    private const int DebounceSeconds = 60;
    private const string TaskKey = "FetchMissingTrailers";

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<NewItemWatcher> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _pendingItemIds = new();
    private Timer? _debounceTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewItemWatcher"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{NewItemWatcher}"/> interface.</param>
    public NewItemWatcher(ILibraryManager libraryManager, IProviderManager providerManager, ITaskManager taskManager, ILogger<NewItemWatcher> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _providerManager.RefreshCompleted += OnRefreshCompleted;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _providerManager.RefreshCompleted -= OnRefreshCompleted;
        _debounceTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _debounceTimer?.Dispose();
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.TriggerOnNewItem)
        {
            return;
        }

        // Only movies/series matter to this plugin, and a virtual item (a missing
        // episode placeholder, for instance) was never actually added to disk.
        if (e.Item.IsVirtualItem || (e.Item is not Movie && e.Item is not Series))
        {
            return;
        }

        _pendingItemIds[e.Item.Id] = 0;
    }

    private void OnRefreshCompleted(object? sender, GenericEventArgs<BaseItem> e)
    {
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.TriggerOnNewItem)
        {
            return;
        }

        // Not an item this watcher itself saw added (or already handled once) -
        // some other, routine refresh of an already-known item, not what this
        // exists to react to.
        if (!_pendingItemIds.TryRemove(e.Argument.Id, out _))
        {
            return;
        }

        _logger.LogInformation(
            "  > New item \"{Name}\" was added and its metadata refresh just finished - scheduling an Enricherr run in {DebounceSeconds}s (reset if another new item shows up first).",
            e.Argument.Name,
            DebounceSeconds);

        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ => TriggerTask(), null, TimeSpan.FromSeconds(DebounceSeconds), Timeout.InfiniteTimeSpan);
    }

    private void TriggerTask()
    {
        try
        {
            var worker = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask.Key == TaskKey);
            if (worker is null)
            {
                _logger.LogWarning("  > Could not find the Enricherr scheduled task to trigger it automatically.");
                return;
            }

            if (worker.State == TaskState.Running)
            {
                _logger.LogInformation("  > A new item triggered an automatic Enricherr run, but one is already in progress - skipping, since the running one will cover it.");
                return;
            }

            _logger.LogInformation("  > Triggering an automatic Enricherr run for newly added item(s).");
            _taskManager.Execute(worker, new TaskOptions());
        }
        catch (Exception ex)
        {
            // Best-effort automatic trigger - any failure here just means this run
            // didn't happen automatically; the next scheduled/manual run still
            // covers the same item, so this is never worth doing anything more
            // disruptive than logging about.
            _logger.LogWarning("  > Failed to trigger an automatic Enricherr run: {Error}", ex.Message);
        }
    }
}
