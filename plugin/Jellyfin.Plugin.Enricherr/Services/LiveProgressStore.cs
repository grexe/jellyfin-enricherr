using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// A snapshot of an in-progress "Fetch Missing Trailers" run, for the settings page to
/// poll and show live - the same shape of counts as <see cref="RunSummary"/> (so the
/// page can reuse its existing rendering), plus which library is currently being
/// processed and how far through the whole run this snapshot was taken.
/// </summary>
public record LiveProgress(
    [property: JsonPropertyName("startedAtUtc")] DateTime StartedAtUtc,
    [property: JsonPropertyName("currentLibrary")] string CurrentLibrary,
    [property: JsonPropertyName("libraryIndex")] int LibraryIndex,
    [property: JsonPropertyName("libraryCount")] int LibraryCount,
    [property: JsonPropertyName("itemsProcessed")] int ItemsProcessed,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("totalMovies")] int TotalMovies,
    [property: JsonPropertyName("scanned")] int Scanned,
    [property: JsonPropertyName("alreadyHadTrailer")] int AlreadyHadTrailer,
    [property: JsonPropertyName("downloaded")] int Downloaded,
    [property: JsonPropertyName("notFound")] int NotFound,
    [property: JsonPropertyName("skipped")] int Skipped,
    [property: JsonPropertyName("renamed")] int Renamed,
    [property: JsonPropertyName("migrated")] int Migrated,
    [property: JsonPropertyName("totalSeries")] int TotalSeries,
    [property: JsonPropertyName("seriesScanned")] int SeriesScanned,
    [property: JsonPropertyName("seriesAlreadyHadTrailer")] int SeriesAlreadyHadTrailer,
    [property: JsonPropertyName("seriesDownloaded")] int SeriesDownloaded,
    [property: JsonPropertyName("seriesNotFound")] int SeriesNotFound,
    [property: JsonPropertyName("seriesSkipped")] int SeriesSkipped,
    [property: JsonPropertyName("moviesScanStarted")] bool MoviesScanStarted,
    [property: JsonPropertyName("seriesScanStarted")] bool SeriesScanStarted,
    [property: JsonPropertyName("upgraded")] int Upgraded,
    [property: JsonPropertyName("seriesUpgraded")] int SeriesUpgraded,
    [property: JsonPropertyName("themeSongAlreadyHad")] int ThemeSongAlreadyHad,
    [property: JsonPropertyName("themeSongDownloaded")] int ThemeSongDownloaded,
    [property: JsonPropertyName("themeSongNotFound")] int ThemeSongNotFound,
    [property: JsonPropertyName("seriesThemeSongAlreadyHad")] int SeriesThemeSongAlreadyHad,
    [property: JsonPropertyName("seriesThemeSongDownloaded")] int SeriesThemeSongDownloaded,
    [property: JsonPropertyName("seriesThemeSongNotFound")] int SeriesThemeSongNotFound,
    [property: JsonPropertyName("seriesRenamed")] int SeriesRenamed,
    [property: JsonPropertyName("seriesSeasonsRenamed")] int SeriesSeasonsRenamed);

/// <summary>
/// Persists a periodically-updated snapshot of the currently-running "Fetch Missing
/// Trailers" task, so the settings page can poll it and show live progress instead of
/// only the final result once the whole run completes. Deliberately separate from
/// <see cref="RunSummaryStore"/>'s completed-run file - a reader only trusts this one
/// when Jellyfin's own <c>ITaskManager</c> confirms the task is actually still
/// running (see <c>EnricherrController.GetLiveProgress</c>), so a leftover snapshot
/// from a crashed run is never mistaken for a live one; this store itself doesn't
/// need to reason about staleness at all.
/// </summary>
public static class LiveProgressStore
{
    private const string FileName = "live-progress.json";

    /// <summary>Writes the given snapshot, overwriting any previous one.</summary>
    public static void Save(string dataFolderPath, LiveProgress progress)
    {
        Directory.CreateDirectory(dataFolderPath);
        File.WriteAllText(Path.Combine(dataFolderPath, FileName), JsonSerializer.Serialize(progress));
    }

    /// <summary>Reads the current snapshot, or null if there isn't one or it can't be read.</summary>
    public static LiveProgress? Load(string dataFolderPath)
    {
        var path = Path.Combine(dataFolderPath, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LiveProgress>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Removes the snapshot, if any - called once a run finishes, best-effort.</summary>
    public static void Clear(string dataFolderPath)
    {
        try
        {
            File.Delete(Path.Combine(dataFolderPath, FileName));
        }
        catch (IOException)
        {
        }
    }
}
