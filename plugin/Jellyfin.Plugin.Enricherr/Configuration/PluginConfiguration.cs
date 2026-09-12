using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Enricherr.Configuration;

/// <summary>
/// Controls which movies get moved into their own subfolder. Jellyfin's local-extras
/// resolver only recognizes a local trailer file when the movie has a dedicated folder
/// (see https://github.com/jellyfin/jellyfin/issues/10077) - a correctly named
/// "&lt;title&gt;-trailer" file sitting in a folder shared by other movies is silently
/// ignored.
/// </summary>
public enum MigrationMode
{
    /// <summary>
    /// Never move any movie. A movie that doesn't already have its own folder is
    /// skipped entirely (logged as a warning) rather than downloading a trailer
    /// Jellyfin could never recognize.
    /// </summary>
    Disabled,

    /// <summary>
    /// Only move movies that already have, or just got, a trailer. The rest of a flat
    /// library is left untouched.
    /// </summary>
    TrailersOnly,

    /// <summary>
    /// Move every movie encountered, regardless of trailer status.
    /// </summary>
    All
}

/// <summary>
/// Plugin configuration for Jellyfin Enricherr.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        RenameOriginal = false;
        RenameSeriesFolders = false;
        MigrateToFolders = MigrationMode.Disabled;
        DryRun = false;
        TriggerLibraryScan = true;
        CookiesFilePath = string.Empty;
        MaxTrailerDurationSeconds = 300;
        LibraryIds = Array.Empty<string>();
        VerboseLogging = false;
        RequestDelaySeconds = 3;
        RetryOnRateLimit = true;
        RateLimitRetryDelayMinutes = 65;
        UpgradeLowQualityTrailers = false;
        MinTrailerResolution = 720;
        AllowUpgradeInOtherLanguage = false;
        FetchThemeSongs = false;
        FixPermissions = false;
        SearchForMissingMetadata = false;
        TmdbApiKey = string.Empty;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the original movie file should be
    /// renamed to match its resolved title (e.g. "Movie Name (Year).ext").
    /// </summary>
    public bool RenameOriginal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a TV series' own top-level folder
    /// should be renamed to match its resolved title (e.g. "Series Name (Year)"), and
    /// its season subfolders renamed to Jellyfin's own canonical "Season NN"/"Specials"
    /// naming (see <see cref="Services.SeriesFileOperations.SeasonFolderName"/>) - based
    /// on Jellyfin's own resolved season number for each (its Season entity's own
    /// IndexNumber), not parsed from the subfolder's own name. A release-style folder
    /// name (tags, resolution, season ranges, ...) can still resolve to the right
    /// trailer - Jellyfin's own provider matching is forgiving enough for search - but
    /// Jellyfin's UI (poster in the collection/list view, displayed title) is driven by
    /// the folder names themselves, which the search/matching step never touches.
    /// Episode files are never renamed or moved - deliberately not attempting that
    /// broader, higher-risk normalization.
    /// </summary>
    public bool RenameSeriesFolders { get; set; }

    /// <summary>
    /// Gets or sets which movies get moved into a dedicated "&lt;title&gt;/&lt;title&gt;.ext"
    /// subfolder so Jellyfin can actually recognize their local trailer.
    /// </summary>
    public MigrationMode MigrateToFolders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a manually triggered run should only
    /// log what it would do, without downloading, renaming, or moving anything.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a Jellyfin library scan should be
    /// triggered after a run that downloaded a trailer or moved a movie, so the
    /// change is picked up immediately instead of waiting for the next scheduled scan.
    /// </summary>
    public bool TriggerLibraryScan { get; set; }

    /// <summary>
    /// Gets or sets the path to a Netscape-format cookies.txt file on the server,
    /// used by yt-dlp for authenticated/age-restricted YouTube access. Unlike the
    /// standalone script, a headless server has no browser profile to read cookies
    /// from directly, so this points at an exported cookie file instead. Leave empty
    /// to fetch without authentication.
    /// </summary>
    public string CookiesFilePath { get; set; }

    /// <summary>
    /// Gets or sets the maximum trailer duration, in seconds, accepted from a search
    /// result. Trailers are rarely longer than this; anything longer is assumed to be
    /// a full movie, compilation, or unrelated video.
    /// </summary>
    public int MaxTrailerDurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the library (VirtualFolder) ids to scan for missing trailers, by
    /// their <c>ItemId</c> as reported by <c>ILibraryManager.GetVirtualFolders()</c>.
    /// An empty array means every library is scanned.
    /// </summary>
    public string[] LibraryIds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to log each rejected search candidate's
    /// specific reason ("Title mismatch: ...", "Duration &gt; 300s: ...", ...). Off by
    /// default: a movie/series that needs several fallback search stages before
    /// finding (or giving up on) a trailer can generate dozens of these lines, which
    /// buries the small number of lines that actually matter for a normal run (what
    /// was searched, what was found or not). Each search stage's overall outcome is
    /// still always logged either way - this only affects the per-candidate detail
    /// within a stage, which is mainly useful when actively diagnosing why a specific
    /// movie/series isn't finding a trailer it should.
    /// </summary>
    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Gets or sets the minimum number of seconds to wait before each yt-dlp
    /// invocation (every search probe and every download attempt). A movie/series
    /// that needs several fallback search stages can trigger a couple dozen separate
    /// yt-dlp calls on its own; across a large library with no pacing at all between
    /// them, this can (and did, live) trip YouTube's own rate limiting, which then
    /// blocks the server's whole session for up to an hour - continuing to hammer it
    /// with more requests during that window doesn't help and risks making it worse.
    /// yt-dlp's own suggested mitigation (its "-t sleep" preset) sleeps up to 20s per
    /// download, tuned for one long-running session working through a playlist, not
    /// this plugin's pattern of many short-lived separate invocations - a plain,
    /// smaller per-invocation delay to actually match that pattern.
    /// </summary>
    public int RequestDelaySeconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a run that gets YouTube-rate-limited
    /// should wait <see cref="RateLimitRetryDelayMinutes"/> and then try the rest of
    /// the run exactly once more, instead of stopping immediately. There's no
    /// reliable way to know when the limit actually lifts - YouTube's own message
    /// just states an upper bound ("...for up to an hour") - so this is a single,
    /// bounded retry rather than a backoff loop: if the retry also gets rate-limited,
    /// the run stops for good rather than waiting and retrying again.
    /// </summary>
    public bool RetryOnRateLimit { get; set; }

    /// <summary>
    /// Gets or sets how long to wait, in minutes, before the single retry described
    /// by <see cref="RetryOnRateLimit"/>. Defaults to a bit past YouTube's own stated
    /// "up to an hour" ceiling, since retrying too early just spends the one retry
    /// hitting the same still-active limit.
    /// </summary>
    public int RateLimitRetryDelayMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a movie/series that already has a
    /// local trailer should still be checked against <see cref="MinTrailerResolution"/>
    /// and re-searched if it falls short - e.g. one downloaded through the mweb
    /// fallback tier (used when YouTube's higher-quality formats need a PO token
    /// this plugin doesn't provide), which can silently land as low as 360p. Backs
    /// the "Update existing trailers" checkbox on the config page. Unlike
    /// <see cref="MinTrailerResolution"/> and <see cref="AllowUpgradeInOtherLanguage"/>,
    /// which are honored on every search regardless of this setting, this one only
    /// controls whether an item that *already has* a trailer is eligible to be
    /// re-examined at all - a fresh item with no trailer yet always gets the same
    /// resolution/language treatment, on or off. Off by default, since turning it on
    /// means re-running the full search/download flow for every under-resolution
    /// trailer on every run until each one clears the bar, not just a one-time pass -
    /// more yt-dlp traffic, and so more rate-limit exposure, until the library
    /// catches up. The existing file is only ever replaced by a genuinely
    /// higher-resolution download (both are probed via ffprobe and compared); a
    /// re-attempt that can't beat it, or gets rate-limited before finding a
    /// candidate, leaves the original trailer untouched.
    /// </summary>
    public bool UpgradeLowQualityTrailers { get; set; }

    /// <summary>
    /// Gets or sets the minimum acceptable trailer resolution, as a frame height in
    /// pixels (e.g. 720 for "720p"). Honored on every search - a fresh item with no
    /// trailer yet keeps trying further sources for a better result until this is
    /// met, the same as <see cref="UpgradeLowQualityTrailers"/> does for an existing
    /// one - so a fresh download doesn't need a later run to reach quality it could
    /// have gotten immediately. The config page presents this as a fixed-step
    /// dropdown (480/720/1080/1440/2160), but any int is accepted here - a value
    /// saved by an older version that isn't one of those steps gets snapped to the
    /// nearest one the next time the page loads.
    /// </summary>
    public int MinTrailerResolution { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a search should prioritize resolution
    /// over the item's preferred language - the audio-track equivalent of
    /// <see cref="MinTrailerResolution"/>'s resolution check, in a sense. Backs the
    /// "Audio" radio switch on the config page ("Preferred language" vs "Best
    /// quality"). Honored on every search, the same way <see cref="MinTrailerResolution"/>
    /// is - not just when re-checking an existing trailer via
    /// <see cref="UpgradeLowQualityTrailers"/>. False (preferred language, the
    /// default) searches in the same native-language-first order as any other
    /// search, which can settle for a mediocre native-language result (the search
    /// loop stops at the first successful download, regardless of resolution)
    /// without ever trying the English stage that often has a genuinely
    /// higher-quality upload available. True (best quality) skips the
    /// native-language stages entirely and searches in English/best-available
    /// directly.
    /// </summary>
    public bool AllowUpgradeInOtherLanguage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to also fetch a local theme song
    /// (<c>theme.mp3</c>) for each movie/series, looked up on
    /// <see href="https://github.com/LizardByte/ThemerrDB">ThemerrDB</see> by TMDb id -
    /// the same community-curated database the Themerr-jellyfin plugin uses, so an
    /// item only gets a theme song here if Themerr would have offered one too. Only the
    /// lookup is shared with Themerr; the download itself goes through this plugin's
    /// own yt-dlp pipeline (rate-limit pacing, retry, client fallback) instead of
    /// Themerr's YoutubeExplode-based downloader, which fails outright on plenty of
    /// videos yt-dlp handles fine. An item with no TMDb id, or no ThemerrDB entry, is
    /// silently skipped - this never blocks or replaces an existing theme.mp3, whether
    /// user-provided or downloaded by Themerr previously. Off by default.
    /// </summary>
    public bool FetchThemeSongs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this plugin may adjust filesystem
    /// permissions (bits and group) on a folder/file it creates or writes into, to
    /// match an already-working reference file (see
    /// <see cref="Services.UnixPermissions.MatchTo"/>) - needed on some setups where
    /// media added by one user/process denies write access to whichever user Jellyfin
    /// itself runs as, even when they share a group. Off by default: this plugin
    /// changing filesystem permissions on its own is an intrusive default for a fresh
    /// install, and a real bug in this exact mechanism (fixed in 1.1.0.4, after
    /// shipping in 0.8.28.0) briefly broke directory access on a live server before
    /// being caught - an admin who wants this should opt in deliberately.
    /// No effect on Windows, where this was always a no-op.
    /// </summary>
    public bool FixPermissions { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to search for and apply better remote
    /// metadata for a movie Jellyfin has no match for at all (<c>ProviderIds</c>
    /// empty) - common for oddly-named releases (scene tags, internal archive/catalog
    /// numbers) that Jellyfin's own provider matching can't make sense of, even
    /// though the title is findable once the noise is stripped (see
    /// <see cref="Services.MissingMetadataMatcher"/>). Deliberately narrow and
    /// conservative: only ever touches an item with NO existing match (never
    /// second-guesses one Jellyfin already made, right or wrong), requires a strict
    /// title similarity plus year match against the search candidate, and a runtime
    /// cross-check (the candidate's own claimed runtime vs. this plugin's own ffprobe
    /// of the local file, not Jellyfin's cached value) before ever applying anything -
    /// unattended automatic metadata matching for the wrong movie is a real risk this
    /// plugin would rather fail closed on than get wrong. Off by default.
    /// </summary>
    public bool SearchForMissingMetadata { get; set; }

    /// <summary>
    /// Gets or sets a TMDb credential (free at
    /// https://www.themoviedb.org/settings/api - either the "API Read Access Token"
    /// (v4 auth, a JWT, the one TMDb's own current API docs use) or the older "API
    /// Key" (v3 auth); <see cref="Services.TmdbAlternativeTitlesClient"/> detects
    /// which was pasted in and authenticates accordingly, since TMDb's settings page
    /// presents both side by side under easily-confused names), used only to fetch a
    /// search candidate's alternate/localized titles while matching missing metadata
    /// (see <see cref="Services.MissingMetadataMatcher"/>). Jellyfin's own bundled
    /// TMDb integration never requests this data and doesn't expose a way to reuse its
    /// own key for it, so this plugin needs its own - deliberately scoped to this one
    /// lookup, not used anywhere else. Only consulted as a rescue path when a
    /// candidate's primary title doesn't already clear the similarity bar; leave empty
    /// to skip alternate-title lookups entirely and judge every candidate on its
    /// primary title alone, same as before this setting existed.
    /// </summary>
    public string TmdbApiKey { get; set; }
}
