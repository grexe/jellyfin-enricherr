# Attribution

## ThemerrDB

Theme song lookups use [ThemerrDB](https://github.com/LizardByte/ThemerrDB) © LizardByte, licensed under
[BSD-3-Clause](https://github.com/LizardByte/ThemerrDB/blob/master/LICENSE) — a community-curated database
mapping movies and TV shows to their theme song, the same one the
[Themerr-jellyfin](https://github.com/LizardByte/Themerr-jellyfin) plugin uses. Only the data is used (a plain
HTTP lookup at runtime, no API key); none of this plugin's own download code is derived from Themerr-jellyfin's
source.

The ThemerrDB logo shown on this plugin's settings page (and in its documentation) is the trademark/copyright of
LizardByte, and is included under fair use for attribution purposes only — to identify the data source, the same
way TMDb's own logo is shown below. This project is not affiliated with, sponsored by, or endorsed by LizardByte.

## TMDb

The optional missing-metadata search checks a candidate's localized titles via [TMDb](https://www.themoviedb.org/)'s
API when a credential is configured. This plugin uses the TMDb API but is not endorsed or certified by TMDb.
