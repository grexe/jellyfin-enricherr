using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Enricherr.Services;

/// <summary>
/// One library's row in the live-progress table - the same shape as the "Overall"
/// tab's per-library rows (name/items/trailers/themeSongs), so the settings page can
/// render both through the same table code. <see cref="Trailers"/>/
/// <see cref="ThemeSongs"/> are null for a library this run hasn't reached yet
/// (rendered client-side as "n/a", not a bare 0 - a library not started is not the
/// same as one genuinely found to have zero coverage).
/// </summary>
public record LiveProgressLibraryRow(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("items")] int Items,
    [property: JsonPropertyName("trailers")] int? Trailers,
    [property: JsonPropertyName("themeSongs")] int? ThemeSongs);

/// <summary>
/// A snapshot of an in-progress "Fetch Missing Trailers" run, for the settings page to
/// poll and show live - one row per library in scope for this run (not just the one
/// currently being processed), plus which library is currently being processed and
/// how far through the whole run this snapshot was taken.
/// </summary>
public record LiveProgress(
    [property: JsonPropertyName("startedAtUtc")] DateTime StartedAtUtc,
    [property: JsonPropertyName("currentLibrary")] string CurrentLibrary,
    [property: JsonPropertyName("libraryIndex")] int LibraryIndex,
    [property: JsonPropertyName("libraryCount")] int LibraryCount,
    [property: JsonPropertyName("itemsProcessed")] int ItemsProcessed,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("libraries")] List<LiveProgressLibraryRow> Libraries);

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
