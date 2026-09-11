using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Enricherr.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Enricherr;

/// <summary>
/// The Jellyfin Enricherr plugin: finds and downloads missing local trailers for movies
/// and TV series from YouTube via yt-dlp, and (optionally) migrates movies into their
/// own folder, which Jellyfin requires to recognize a local movie trailer at all
/// (see https://github.com/jellyfin/jellyfin/issues/10077).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Jellyfin Enricherr";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("04035fb7-8ca5-4bbf-a65b-7608c36e4e04");

    /// <inheritdoc />
    public override string Description =>
        "Finds and downloads missing local trailers for movies and TV series from YouTube via yt-dlp.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
