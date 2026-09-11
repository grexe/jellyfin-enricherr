#!/usr/bin/env bash
# Builds a Release zip for the Jellyfin plugin and prints the manifest.json entry to
# add/update by hand. Run from anywhere; paths below are relative to this script.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

CSPROJ="Jellyfin.Plugin.Enricherr/Jellyfin.Plugin.Enricherr.csproj"
VERSION="$(grep -m1 '<Version>' "$CSPROJ" 2>/dev/null | sed -E 's/.*<Version>(.*)<\/Version>.*/\1/' || true)"
if [ -z "$VERSION" ]; then
    # No <Version> in the csproj - fall back to build.yaml's version field.
    VERSION="$(grep -m1 '^version:' build.yaml | sed -E 's/version: *"(.*)"/\1/')"
fi
TARGET_ABI="$(grep -m1 '^targetAbi:' build.yaml | sed -E 's/targetAbi: *"(.*)"/\1/')"

# Keep build.yaml's version in sync with the csproj automatically - the csproj's
# <Version> is what actually ends up baked into the compiled assembly (what Jellyfin
# reports/tracks for an installed plugin), so it must be the single source of truth.
# A drift here previously left the assembly at .NET's default "1.0.0.0" while
# build.yaml/manifest.json said otherwise, which caused real instability (dashboard
# reverting to "1.0.0.0" after a task run or restart, and uninstall failing outright).
sed -i.bak -E "s/^version: \".*\"/version: \"$VERSION\"/" build.yaml && rm -f build.yaml.bak

echo "Packaging version $VERSION..."

# Clean rebuild - dotnet's incremental build can otherwise skip recompiling on rapid
# successive edits (observed: an unchanged-checksum zip after real code changes).
rm -rf publish dist Jellyfin.Plugin.Enricherr/bin Jellyfin.Plugin.Enricherr/obj
dotnet publish "$CSPROJ" -c Release -o publish

mkdir -p dist
ZIP_NAME="Jellyfin.Plugin.Enricherr_${VERSION}.zip"
ZIP_PATH="dist/${ZIP_NAME}"

# Only the plugin's own assembly (+ pdb for stack traces) ships - everything else
# (Jellyfin.Controller/Model/Common, ASP.NET Core) is provided by the Jellyfin host.
(cd publish && zip -q "../${ZIP_PATH}" Jellyfin.Plugin.Enricherr.dll Jellyfin.Plugin.Enricherr.pdb)

CHECKSUM="$(md5 -q "$ZIP_PATH" 2>/dev/null || md5sum "$ZIP_PATH" | cut -d' ' -f1)"
TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

# Mirrors manifest.json to docs/jellyfin (no extension - GitHub Pages serves it
# as-is) so kino.sen-labs.org/jellyfin is a short, branded repository URL people
# can add in Jellyfin instead of the raw githubusercontent.com path. Not a real
# HTTP redirect (Jellyfin's fetch can't tell the difference either way) - chosen
# specifically to avoid needing kino's DNS record switched to Cloudflare-proxied,
# which would risk the GitHub Pages HTTPS cert that took real effort to get
# working. IMPORTANT: this copies manifest.json's CURRENT content, so update
# manifest.json's versions array with this release's entry BEFORE running this
# script, not after - copying stale content here is a silent miss, not an error.
cp manifest.json ../docs/jellyfin

echo
echo "Built: $ZIP_PATH"
echo "Checksum (MD5): $CHECKSUM"
echo
echo "Add/replace this entry in manifest.json's \"versions\" array:"
cat <<EOF
    {
      "version": "$VERSION",
      "changelog": "See commit history.",
      "targetAbi": "$TARGET_ABI",
      "sourceUrl": "https://raw.githubusercontent.com/grexe/jellyfin-enricherr/main/plugin/dist/${ZIP_NAME}",
      "checksum": "$CHECKSUM",
      "timestamp": "$TIMESTAMP"
    }
EOF
