<p align="center">
  <img src="images/logo.png" alt="Jellyfin Enricherr logo" width="200" />
</p>

# Jellyfin Enricherr (Jellyfin plugin)

Finds and downloads missing local trailers - for movies and TV series - from YouTube
via `yt-dlp`, running natively in-process on the Jellyfin server, with sequel-aware
title matching and automatic folder migration for movies - Jellyfin only recognizes a
local trailer file when the movie has its own dedicated folder
([jellyfin/jellyfin#10077](https://github.com/jellyfin/jellyfin/issues/10077)); a
series always already has one, so that step doesn't apply there.

This is a from-scratch C# port of the standalone `jellyfin-trailer-fetcher` Python
script (its source has since been removed from this repository - fully superseded,
see the root README's "Why a plugin, not a script?" for the history), not a wrapper
around it - running server-side changes the shape of the problem enough that a
straight rewrite made more sense than shelling out.

## Why a native rewrite, not a wrapper

The script was built to run on a separate machine (a Mac) against a remote Jellyfin
server over its NAS mount, and that shaped a lot of its design: `PATH_MAPPINGS` to
translate NAS paths to a local mount, a Jellyfin API key for every read *and* write,
`--cookie-browser` to read a logged-in browser's cookie jar, and REST calls (including
a full library scan) to get Jellyfin to notice anything changed.

None of that applies once the code runs *inside* Jellyfin's own process:

- **No path translation.** The plugin sees the exact same filesystem Jellyfin does -
  `ILibraryManager` items already carry the real, local path.
- **No API key, no REST round-trips to itself.** Library queries, refreshes, and scans
  go through `ILibraryManager`/`IServerApplicationHost` directly.
- **No browser cookie jar.** A headless server has no logged-in Firefox profile to read
  from, so authenticated/age-restricted YouTube access instead uses an exported
  Netscape-format `cookies.txt`, uploaded through the settings page.
- **Per-library scoping** becomes possible for free, since the plugin can query
  `ILibraryManager.GetVirtualFolders()` directly instead of guessing from paths.

The title-matching, filtering, and folder-migration *logic* itself - the part that took
the most iteration to get right (sequel-vs-original confusion, trusting a mismatched
title's own other fields, etc.) - carries over essentially unchanged, just reimplemented
in C#.

### Why a plain `Process` wrapper, not a yt-dlp .NET library

[`Ytdlp.NET`](https://github.com/manusoft/yt-dlp-wrapper) (a fluent .NET wrapper around
the yt-dlp CLI) was evaluated and rejected. It has fluent methods for some of what's
needed (cookies file, `--extractor-args`, `--ffmpeg-location`), but not for
`--remote-components`/`--js-runtimes` (which YouTube extraction actually depends on
here) - those still need its raw-command escape hatch. More importantly, its metadata
extraction is single-URL only; probing a `ytsearch5:` query for up to 5 candidates to
filter in C# (this plugin's whole match-filter replication strategy - see
`Services/YtDlpClient.cs`) isn't a built-in feature, so the JSON-lines parsing would
still need to be hand-written on top of it. Cancellation is also only documented for
its raw-execution path, not its higher-level download methods, and this task relies on
the scheduled task's `CancellationToken` actually killing an in-flight yt-dlp process.
Net: it would have replaced a modest amount of argument-list plumbing while leaving the
actual custom logic - and its own 18-star, single-maintainer risk profile - in place, so
the plugin sticks with `System.Diagnostics.Process` directly.

## Current status

| Area | Status |
|---|---|
| Plugin/task/config wiring | ✅ Working - settings page loads/saves, scheduled task registers |
| Cookie file upload | ✅ Working |
| Per-library scan scoping | ✅ Working |
| Dedicated log file | ✅ Working |
| Trailer search/filter/download (movies) | ✅ Working - no container customization needed, see Requirements |
| Folder migration & rename (movies) | ✅ Working |
| Trailer search/filter/download (TV series) | ✅ Working - series-level only, see TV series below |

## Requirements

`FetchTrailersTask` shells out to a `yt-dlp` executable rather than reimplementing
YouTube extraction in C# - the same tool the standalone script uses, just invoked as a
subprocess instead of through its Python API (there's no C# equivalent, and match
filtering is replicated by probing candidates with `--dump-json` before downloading the
one that passes). yt-dlp also needs a JS runtime (`deno`) to solve YouTube's player
challenges.

**Neither needs to be installed on the server, and there's no setting for it.**
`Services/DependencyProvisioner.cs` always downloads both directly from their own GitHub
releases into the plugin's data folder the first time they're needed, verifies each
download's checksum, and self-updates yt-dlp (via its own `-U`) at most once every 24
hours - yt-dlp needs frequent updates to keep working against YouTube's changes, which
is also why neither is bundled inside the plugin's own release zip (a copy frozen at
plugin-release time would go stale in weeks). This requires outbound internet access
from wherever the Jellyfin server process runs, which it already needs anyway to reach
YouTube itself (and to check for plugin/Jellyfin updates in the first place) - not
something this plugin should need to work around. ffmpeg does *not* get this treatment -
the plugin points yt-dlp at Jellyfin's own configured ffmpeg binary
(`IMediaEncoder.EncoderPath`) instead.

There's deliberately no setting to point at a different/system yt-dlp installation:
letting that vary would mean supporting whatever combination of yt-dlp version and flags
a user happens to have installed, instead of the one version+flag combination this
plugin is actually built and tested against. If provisioning fails (e.g. no internet
egress), the plugin logs a clear error per attempted download rather than failing
silently - check `enricherr.log` (see Logging below).

### Evaluated and rejected: MeTube

[MeTube](https://github.com/alexta69/metube) (a yt-dlp web UI/API, run as its own
container) was also considered as an alternative to a local yt-dlp. Rejected: it's a
download-only API with no equivalent of a metadata-only probe (`--dump-json` without
downloading), which this plugin's candidate filtering depends on - replicating that
would mean reverse-engineering a second, unofficial API surface instead of just calling
yt-dlp directly. It would also mean deploying and networking a second container, mapping
its downloads folder onto the same volume as the media library, and configuring cookies
separately from this plugin's own cookie upload - real setup friction for what the
self-managed-binary approach above already solves with zero extra services.

## TV series

Series-level trailers only, for now - checked Jellyfin's actual entity model before
building anything: `Series` has its own `LocalTrailers`/`RemoteTrailers`, the same
shape as `Movie`, so it maps cleanly onto the existing approach. `Season` does not
expose an equivalent property at all, even though Jellyfin's docs show a `trailers/`
folder example nested under a season folder - whether that's actually reachable through
the API, or just placed there and attached to the series as a whole regardless, isn't
confirmed yet, so season-level trailers aren't implemented until that's verified against
a real library.

Title/year resolution and source-query building (`Services/ItemMetadata.cs`,
`Services/TrailerSources.cs`) are shared between movies and series - both operate on
`BaseItem` (Name, OriginalTitle, ProductionYear, RemoteTrailers, ...) with no movie- or
series-specific behavior baked in, so a first cut that duplicated them into
movie-specific and series-specific copies (verified byte-for-byte identical aside from
variable names and comments) was refactored back into one shared implementation each -
keeping the duplicates would have meant every future fix to either needing to be applied
twice. What's kept genuinely separate is what actually differs:
`ProcessMovieAsync`/`ProcessSeriesAsync` in `FetchTrailersTask.cs` are distinct
orchestration methods, since a movie additionally needs `MovieFileOperations` for
rename/migrate (see jellyfin/jellyfin#10077 above) and file-specific validity checking,
neither of which a series - always already in its own dedicated folder - needs at all.

## Installing for testing

### Option A - quick local copy (fastest iteration)

```bash
./package.sh
```

This produces `dist/Jellyfin.Plugin.Enricherr_<version>.zip`. Unzip it into your
Jellyfin server's plugin directory, in its own version-named subfolder:

```
<jellyfin data dir>/plugins/Jellyfin Enricherr_<version>/Jellyfin.Plugin.Enricherr.dll
<jellyfin data dir>/plugins/Jellyfin Enricherr_<version>/Jellyfin.Plugin.Enricherr.pdb
```

Then restart Jellyfin. `<jellyfin data dir>` is wherever Jellyfin's `ProgramDataPath` is
mounted for your install - for a Docker/TrueNAS SCALE app, that's the container path
your app's config volume is mapped to (commonly `/config` inside the container); check
your app's storage/volume mapping to find the host path. Repeat after every change: no
update notification, no version tracking - just overwrite and restart.

### Option B - plugin repository (the real install/update path)

This uses Jellyfin's normal install/update mechanism instead of manual file copying.

1. Run `./package.sh` and copy the printed entry into `manifest.json`'s `versions`
   array (replacing the previous entry, or adding a new one to keep history).
2. Commit `dist/` and `manifest.json`, push.
3. In Jellyfin: **Dashboard → Plugins → Repositories → Add Repository**, using this
   raw URL:
   ```
   https://raw.githubusercontent.com/grexe/jellyfin-enricherr/main/plugin/manifest.json
   ```
4. **Dashboard → Plugins → Catalog** should now list "Jellyfin Enricherr" - install it
   from there. Future `package.sh` + manifest update + push cycles show up as a normal
   update in the Jellyfin dashboard.

## Library scoping

The settings page lists every configured Jellyfin library (fetched live via
`GET /Enricherr/Libraries`, backed by `ILibraryManager.GetVirtualFolders()`).
Leave all unchecked to scan every library; check specific ones to scope
`FetchTrailersTask`'s query to just those (`InternalItemsQuery.Parent`, one query per
selected library).

## Logging

This plugin's log entries (from `FetchTrailersTask`, `EnricherrController`, and
anything else in the `Jellyfin.Plugin.Enricherr` namespace) are mirrored into
their own file, `<plugin data folder>/enricherr.log`, in addition to Jellyfin's
main server log - so a run can be inspected without wading through unrelated server
noise. Implemented as a scoped `ILoggerProvider` (`Logging/PluginFileLoggerProvider.cs`,
registered via `PluginServiceRegistrator`) that filters by log category rather than
requiring a second, duplicate logging call at every call site - any future code that
injects the normal `ILogger<T>` gets this for free.

## Development

```bash
dotnet build          # verify it compiles against the real Jellyfin 10.11.11 packages
dotnet publish -c Release -o publish   # what package.sh does internally
```

No local Jellyfin server is required to build or verify compilation - only to actually
run the plugin.
