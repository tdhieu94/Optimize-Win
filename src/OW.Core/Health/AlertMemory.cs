using System.Text.Json;

namespace OW.Core.Health;

/// <summary>
/// Remembers which alerts were already announced (in a small JSON file in Hieu's profile) so the weekly
/// and sign-in check does not repeat the same warning every time.
/// </summary>
public static class AlertMemory
{
    private sealed record Entry(string Id, AlertLevel Level, DateTime AtUtc);

    public static Dictionary<string, (AlertLevel Level, DateTime AtUtc)> Load(string path)
    {
        var result = new Dictionary<string, (AlertLevel, DateTime)>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
            {
                foreach (var e in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [])
                {
                    if (!string.IsNullOrEmpty(e.Id))
                    {
                        result[e.Id] = (e.Level, DateTime.SpecifyKind(e.AtUtc, DateTimeKind.Utc));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken file only means an alert may be shown once more.
        }

        return result;
    }

    /// <summary>
    /// Keeps only alerts that are still current (so a problem that went away and comes back is announced again)
    /// and records the ones just announced.
    /// </summary>
    public static IReadOnlyList<(string Id, AlertLevel Level, DateTime AtUtc)> Next(
        IReadOnlyDictionary<string, (AlertLevel Level, DateTime AtUtc)> previous,
        IReadOnlyList<HealthAlert> current,
        IReadOnlyList<HealthAlert> announced,
        DateTime nowUtc)
    {
        var shown = announced.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var list = new List<(string, AlertLevel, DateTime)>();
        foreach (var a in current)
        {
            if (shown.Contains(a.Id))
            {
                list.Add((a.Id, a.Level, nowUtc));
            }
            else if (previous.TryGetValue(a.Id, out var seen))
            {
                list.Add((a.Id, seen.Level, seen.AtUtc));
            }
        }

        return list;
    }

    public static void Save(string path, IReadOnlyList<(string Id, AlertLevel Level, DateTime AtUtc)> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries.Select(e => new Entry(e.Id, e.Level, e.AtUtc)).ToList()));
        File.Move(tmp, path, overwrite: true);
    }
}
