using System.Text.Json;
namespace HardwareLens.Core;

public sealed record DiffLine(string Path, string Kind, string? Before, string? After);

/// <summary>Compares two exported JSON reports field by field, ignoring values that change on every run (times, live readings, free space).</summary>
public static class ReportDiff
{
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    { "generatedAt", "collectedAt", "appVersion", "live", "observations", "uptime", "uptimeSeconds", "bootTime", "lastBoot", "freeBytes", "usedBytes", "availableBytes", "freePercent", "usedPercent", "elapsedSeconds", "collectionSeconds", "temperatureC", "powerOnHours", "batteryPercent", "chargePercent" };

    public static List<DiffLine> Compare(string jsonA, string jsonB)
    {
        var a = new SortedDictionary<string, string>(StringComparer.Ordinal); var b = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using (var da = JsonDocument.Parse(jsonA)) Flatten(da.RootElement, "", a, 0);
        using (var db = JsonDocument.Parse(jsonB)) Flatten(db.RootElement, "", b, 0);
        var res = new List<DiffLine>();
        foreach (var k in a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            bool ha = a.TryGetValue(k, out var va), hb = b.TryGetValue(k, out var vb);
            if (ha && hb) { if (va != vb) res.Add(new DiffLine(k, "changed", va, vb)); }
            else if (ha) res.Add(new DiffLine(k, "removed", va, null)); else res.Add(new DiffLine(k, "added", null, vb));
        }
        return res;
    }

    static void Flatten(JsonElement e, string path, IDictionary<string, string> into, int depth)
    {
        if (depth > 12) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) { if (Skip.Contains(p.Name)) continue; Flatten(p.Value, path.Length == 0 ? p.Name : path + " › " + p.Name, into, depth + 1); }
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var it in e.EnumerateArray())
                {
                    string label = (it.ValueKind == JsonValueKind.Object ? new[] { "name", "model", "description", "caption", "label", "id" }.Select(n => it.EnumerateObject().FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase))).Where(x => x.Value.ValueKind == JsonValueKind.String).Select(x => x.Value.GetString()).FirstOrDefault() : null) ?? (i + 1).ToString();
                    Flatten(it, path + " [" + label + "]", into, depth + 1); i++;
                }
                break;
            case JsonValueKind.Null: break;
            default: into[path] = e.ToString(); break;
        }
    }
}
