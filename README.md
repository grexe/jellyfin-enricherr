<p align="center">
  <img src="plugin/images/logo.png" alt="Jellyfin Enricherr logo" width="140" />
</p>

<h1 align="center">Jellyfin Enricherr</h1>

<p align="center">
  A Jellyfin plugin that finds and downloads missing local trailers and theme songs for movies and TV series, and
  cleans up messy release-style folder/file names.
</p>

<p align="center">
  <a href="https://kino.sen-labs.org/"><strong>Documentation</strong></a>
</p>

Jellyfin Enricherr runs as a scheduled task inside your Jellyfin server. For every movie or series without a local
trailer, it tries Jellyfin's own `RemoteTrailers` link first, then falls back to a multi-stage YouTube search with
sequel-aware title matching and duration/keyword filtering, downloads the best-quality match it can get via
`yt-dlp`, and saves it next to your media (`<title>-trailer.mp4`) so every client - including ones that can't stream
remote trailers, like the webOS app - plays it locally.

## Why a plugin, not a script?

This project started as a standalone Python script (`jellyfin-trailer-fetcher`) you'd run by hand or cron against a
Jellyfin server, authenticating over its HTTP API with path mappings to translate NAS paths to a local mount. Once
the plugin fully superseded it - running inside Jellyfin itself on Jellyfin's own scheduler, managing its own
`yt-dlp`/`deno`, configured through Jellyfin's dashboard instead of a `.env` file - the script's source was removed
from this repository; this section stays as a record of where the plugin came from.

## Features

- **Movies and TV series** - not just movies.
- **Official trailer first, YouTube search as fallback** - uses Jellyfin's own `RemoteTrailers` metadata before
  falling back to a multi-stage YouTube search.
- **Sequel- and franchise-aware title matching**, with duration and keyword (`trailer`/`teaser`/`vorschau`/...)
  filtering to reject unrelated search results.
- **Quality-aware downloads** - uses `yt-dlp`'s true best-quality selector, with an optional pass to upgrade
  existing low-quality trailers (e.g. ones that landed on a low-quality fallback format) once a better one is found.
- **Rate-limit aware** - paces requests to avoid tripping YouTube's own rate limiting, and if it happens anyway,
  waits and retries the rest of the run once before giving up, rather than hammering an active limit.
- **Optional file organisation** - can rename the original movie file to match its resolved title, and/or migrate a
  movie into its own subfolder, which Jellyfin requires to recognize a local trailer at all when movies otherwise
  share a flat folder ([jellyfin/jellyfin#10077](https://github.com/jellyfin/jellyfin/issues/10077)).
- **Clean series/season folder names** - can rename a TV series' own top-level folder to its resolved title and its
  season subfolders to Jellyfin's canonical `Season NN` naming, fixing a messy release-style folder name (tags,
  resolution, season ranges, ...) that confuses Jellyfin's own poster match and displayed title even when trailer
  search itself already works fine. Episode files are never touched.
- **Loose subtitle renaming** - detects a release-named subtitle file sitting directly in a movie's own folder
  (rather than matching the movie's own filename) and renames it to match, so Jellyfin recognizes it as an
  external subtitle.
- **Missing-metadata rescue** - for a movie Jellyfin has no metadata match for at all (an oddly-named release that
  defeats Jellyfin's own matching even though the real title is findable once release noise is stripped),
  searches using this plugin's own cleaned-up title and applies a match, but only when it's confident - checked
  against the candidate's title, its actual runtime via `ffprobe`, and its year, optionally cross-checked against
  TMDb's own localized titles too.
- **Theme songs** - optionally fetches a local `theme.mp3` too, looked up on
  [ThemerrDB](https://github.com/LizardByte/ThemerrDB) (the same curated database the
  [Themerr-jellyfin](https://github.com/LizardByte/Themerr-jellyfin) plugin uses) and downloaded through this
  plugin's own hardened `yt-dlp` pipeline instead of Themerr's less reliable downloader.
- **Runs on its own schedule, or the moment a new item is added** - in addition to a manual run or its own
  schedule, can optionally trigger a run as soon as a newly added movie/series' own metadata refresh finishes,
  debounced so a bulk import triggers one run, not one per item.
- **Cookies support** for authenticated/age-restricted YouTube access.
- **Dry-run mode** to preview a run without downloading, renaming, or moving anything.
- **Live statistics and per-run summary** on the settings page - live per-library progress while a run is going,
  overall coverage, and the last completed run's own numbers and stop reason.
- **Debug picker** - a file/folder browser on the settings page to run this plugin's own processing against just
  one movie, without waiting on or otherwise affecting a full scan.

## Requirements

- Jellyfin 10.11.x — verified working through 12.0
- Outbound internet access from the server (to reach YouTube and to download the plugin's managed `yt-dlp`/`deno`
  binaries on first run)

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add a repository with this URL:
   ```
   https://kino.sen-labs.org/jellyfin
   ```
2. Go to **Catalog**, find **Jellyfin Enricherr** under General, and install it.
3. Restart Jellyfin.
4. Open the plugin's settings page (**Dashboard → Plugins → Jellyfin Enricherr**) to configure it, and/or run the
   **Fetch Theme Music and Trailers** scheduled task (**Dashboard → Scheduled Tasks**) to try it.

## Configuration

Settings are grouped on the plugin's page:

- **Scanning** - whether to trigger a Jellyfin library scan after changes or automatically when a new movie/series
  is added, the maximum trailer duration to accept, and which libraries to scan.
- **Audio/Video** - the minimum acceptable trailer resolution and audio-language preference, honored on every
  search, plus whether to also re-check/upgrade existing trailers that fall short.
- **Organisation** - renaming the original movie file, migrating movies into their own folder, renaming loose
  subtitle files, cleaning up series/season folder names, and the missing-metadata rescue search (with an
  optional TMDb credential for localized-title matching).
- **Theme Songs** - whether to also fetch a local theme song via ThemerrDB.
- **Network** - a cookies file for authenticated/age-restricted access, pacing between requests, and the
  rate-limit retry behavior.
- **Debugging and Testing** - dry-run mode, verbose per-candidate logging, and a file/folder browser to run
  against a single movie.

See the [full configuration reference](https://kino.sen-labs.org/configuration.html) for
what every setting does, and the [documentation](https://kino.sen-labs.org/) generally for
how the plugin behaves and troubleshooting help.

## Building from source

```bash
cd plugin
./package.sh
```

Builds the plugin and produces a versioned zip under `plugin/dist/`. See
[`plugin/Jellyfin.Plugin.Enricherr/`](plugin/Jellyfin.Plugin.Enricherr/) for the source.

## The legacy standalone script

This plugin grew out of an earlier standalone Python script (`jellyfin-trailer-fetcher`) that ran against a
Jellyfin server over its HTTP API. Once the plugin fully superseded it, the script's source was removed from this
repository - see "Why a plugin, not a script?" above for why.

## Credits

Theme song lookups use [ThemerrDB](https://github.com/LizardByte/ThemerrDB) © LizardByte, licensed under
[BSD-3-Clause](https://github.com/LizardByte/ThemerrDB/blob/master/LICENSE) - a community-curated, publicly
hosted database mapping movies/TV shows to their theme song's YouTube URL, the same one the
[Themerr-jellyfin](https://github.com/LizardByte/Themerr-jellyfin) plugin uses. Only the data is used (a plain
HTTP lookup at runtime, no API key); none of this plugin's download code is derived from Themerr-jellyfin's own
source, and this project isn't affiliated with or endorsed by LizardByte.
