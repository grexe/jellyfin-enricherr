using Jellyfin.Plugin.Enricherr.ScheduledTasks;
using Jellyfin.Plugin.Enricherr.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Enricherr;

/// <summary>
/// Registers this plugin's own additional services into Jellyfin's DI container -
/// the modern replacement for the older Emby-style <c>IServerEntryPoint</c>.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Starts automatically alongside the server and can subscribe to library
        // events for the lifetime of the process.
        serviceCollection.AddHostedService<NewItemWatcher>();

        // Jellyfin's own IScheduledTask discovery constructs its own instance for
        // the actual scheduled-task registry, entirely separately from this one -
        // safe, since FetchTrailersTask holds no per-instance mutable state of its
        // own (every field is a read-only, DI-provided singleton dependency, and
        // RunForSingleItemAsync builds its own fresh stats/yt-dlp client per call).
        // Registered here purely so EnricherrController can call
        // RunForSingleItemAsync directly for the settings page's debug file picker,
        // without needing to go through the scheduled-task machinery for what's
        // just a one-off, single-item diagnostic run.
        serviceCollection.AddSingleton<FetchTrailersTask>();
    }
}
