using System.Management;
namespace HardwareLens.Windows;

/// <summary>Row of WMI/CIM properties copied out immediately so COM objects are released quickly.</summary>
internal sealed class Row
{
    readonly Dictionary<string, object?> v = new(StringComparer.OrdinalIgnoreCase);
    public Row(ManagementBaseObject o) { foreach (PropertyData p in o.Properties) { try { v[p.Name] = p.Value; } catch { v[p.Name] = null; } } }
    public object? this[string n] => v.TryGetValue(n, out var x) ? x : null;
    public string? Str(string n) { var s = this[n] switch { null => null, string t => t, var o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) }; s = s?.Trim(); return string.IsNullOrEmpty(s) ? null : s; }
    public ulong? U64(string n) { try { return this[n] switch { null => null, ulong u => u, string s when ulong.TryParse(s, out var p) => p, var o => Convert.ToUInt64(o) }; } catch { return null; } }
    public long? I64(string n) { try { return this[n] switch { null => null, var o => Convert.ToInt64(o) }; } catch { return null; } }
    public int? Int(string n) { var x = I64(n); return x is null or > int.MaxValue or < int.MinValue ? null : (int)x; }
    public bool? Bool(string n) => this[n] is bool b ? b : null;
    public ushort[]? U16Array(string n) => this[n] switch { ushort[] a => a, Array a => a.Cast<object>().Select(Convert.ToUInt16).ToArray(), _ => null };
    public DateTime? Date(string n) { var s = Str(n); if (s == null) return null; try { return ManagementDateTimeConverter.ToDateTime(s); } catch { return null; } }
}

internal static class Wmi
{
    public static List<Row> Query(string ns, string wql, double timeoutSeconds = 20)
    {
        var res = new List<Row>();
        var scope = new ManagementScope(ns, new ConnectionOptions { Timeout = TimeSpan.FromSeconds(timeoutSeconds) });
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(timeoutSeconds), ReturnImmediately = false });
        using var coll = searcher.Get();
        foreach (ManagementBaseObject o in coll) { using (o) res.Add(new Row(o)); }
        return res;
    }
}
