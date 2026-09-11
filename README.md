<p align="center">
  <img src="plugin/images/logo.png" alt="Jellyfin Enricherr logo" width="140" />
</p>

<h1 align="center">Jellyfin Enricherr</h1>

<p align="center">
  A Jellyfin plugin that finds and downloads missing local trailers and theme songs for movies and TV series, and
  cleans up messy release-style folder/file names.
</p>

<p align="center">
  <a href="https://grexe.github.io/jellyfin-enricherr/"><strong>Documentation</strong></a>
</p>

Jellyfin Enricherr runs as a scheduled task inside your Jellyfin server. For every movie or series without a local
trailer, it tries Jellyfin's own `RemoteTrailers` link first, then falls back to a multi-stage YouTube search with
sequel-aware title matching and duration/keyword filtering, downloads the best-quality match it can get via
`yt-dlp`, and saves it next to your media (`<title>-trailer.mp4`) so every client - including ones that can't stream
remote trailers, like the webOS app - plays it locally.

## Why a plugin, not a script?

This project started as a standalone Python script you'd run by hand or cron against a Jellyfin server. That script
still lives in this repository (`src/`, `pyproject.toml`, `tests/`) for reference, but it's superseded: the plugin
does everything the script did and more, runs inside Jellyfin itself on Jellyfin's own scheduler, manages its own
`yt-dlp`/`deno` (no manual `ffmpeg`/`yt-dlp` install or environment setup), and is configured through Jellyfin's
dashboard instead of a `.env` file.

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
- **Theme songs** - optionally fetches a local `theme.mp3` too, looked up on
  [ThemerrDB](https://github.com/LizardByte/ThemerrDB) (the same curated database the
  [Themerr-jellyfin](https://github.com/LizardByte/Themerr-jellyfin) plugin uses) and downloaded through this
  plugin's own hardened `yt-dlp` pipeline instead of Themerr's less reliable downloader.
- **Cookies support** for authenticated/age-restricted YouTube access.
- **Dry-run mode** to preview a run without downloading, renaming, or moving anything.
- **Per-run summary** on the settings page - what was found, downloaded, skipped, or upgraded, and why a run
  stopped early if it did.

## Requirements

- Jellyfin 10.11.x
- Outbound internet access from the server (to reach YouTube and to download the plugin's managed `yt-dlp`/`deno`
  binaries on first run)

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add a repository with this URL:
   ```
   https://raw.githubusercontent.com/grexe/jellyfin-enricherr/main/plugin/manifest.json
   ```
2. Go to **Catalog**, find **Jellyfin Enricherr** under General, and install it.
3. Restart Jellyfin.
4. Open the plugin's settings page (**Dashboard → Plugins → Jellyfin Enricherr**) to configure it, and/or run the
   **Fetch Theme Music and Trailers** scheduled task (**Dashboard → Scheduled Tasks**) to try it.

## Configuration

Settings are grouped on the plugin's page:

- **Scanning** - which libraries to scan, whether to trigger a Jellyfin library scan after changes, and the
  maximum trailer duration to accept.
- **Audio/Video** - the minimum acceptable trailer resolution and audio-language preference, honored on every
  search, plus whether to also re-check/upgrade existing trailers that fall short.
- **Organisation** - renaming the original movie file, migrating movies into their own folder, and cleaning up
  series/season folder names.
- **Theme Songs** - whether to also fetch a local theme song via ThemerrDB.
- **Network** - a cookies file for authenticated/age-restricted access, pacing between requests, and the
  rate-limit retry behavior.
- **Debugging and Testing** - dry-run mode and verbose per-candidate logging.

See the [full configuration reference](https://grexe.github.io/jellyfin-enricherr/configuration.html) for
what every setting does, and the [documentation](https://grexe.github.io/jellyfin-enricherr/) generally for
how the plugin behaves and troubleshooting help.

## Building from source

```bash
cd plugin
./package.sh
```

Builds the plugin and produces a versioned zip under `plugin/dist/`. See
[`plugin/Jellyfin.Plugin.Enricherr/`](plugin/Jellyfin.Plugin.Enricherr/) for the source.

## The legacy standalone script

The original Python script this plugin grew out of is still in this repository - see the root
[`src/`](src/) and [`pyproject.toml`](pyproject.toml). It's no longer actively developed; new work goes into the
plugin.

## Credits

Theme song lookups use [ThemerrDB](https://github.com/LizardByte/ThemerrDB) © LizardByte, licensed under
[BSD-3-Clause](https://github.com/LizardByte/ThemerrDB/blob/master/LICENSE) - a community-curated, publicly
hosted database mapping movies/TV shows to their theme song's YouTube URL, the same one the
[Themerr-jellyfin](https://github.com/LizardByte/Themerr-jellyfin) plugin uses. Only the data is used (a plain
HTTP lookup at runtime, no API key); none of this plugin's download code is derived from Themerr-jellyfin's own
source, and this project isn't affiliated with or endorsed by LizardByte.
