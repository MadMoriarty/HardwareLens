using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
namespace HardwareLens.Windows;

public sealed class ProblemEvent
{
    public DateTimeOffset Time; public string Log = "", Source = "", Plain = "", Detail = ""; public int Id; public string? App;
}

/// <summary>Reads recent critical/error events from the Application and System logs (readable without administrator rights) and describes them in plain English.</summary>
public static class EventProblems
{
    public static List<ProblemEvent> Recent(int hours = 24, int max = 150)
    {
        var res = new List<ProblemEvent>();
        foreach (var log in new[] { "Application", "System" })
        {
            try
            {
                var q = new EventLogQuery(log, PathType.LogName, $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {hours * 3600000L}]]]") { ReverseDirection = true };
                using var rd = new EventLogReader(q);
                for (EventRecord? e = rd.ReadEvent(); e != null && res.Count < max * 2; e = rd.ReadEvent())
                {
                    using (e)
                    {
                        string src = e.ProviderName ?? ""; string msg = ""; try { msg = e.FormatDescription() ?? ""; } catch { }
                        string first = msg.Split('\n').FirstOrDefault()?.Trim() ?? "";
                        var pe = new ProblemEvent { Time = e.TimeCreated ?? DateTime.Now, Log = log, Source = src, Id = e.Id, Detail = first.Length > 220 ? first[..220] + "…" : first };
                        Describe(pe, msg); res.Add(pe);
                    }
                }
            }
            catch { }
        }
        return res.OrderByDescending(r => r.Time).Take(max).ToList();
    }

    static void Describe(ProblemEvent p, string msg)
    {
        string s = p.Source;
        if (s == "Application Error") { var m = Regex.Match(msg, @"Faulting application name: ([^,\r\n]+)"); p.App = m.Success ? m.Groups[1].Value : null; p.Plain = $"{p.App ?? "An app"} crashed"; }
        else if (s == "Application Hang") { var m = Regex.Match(msg, @"program (\S+) version"); p.App = m.Success ? m.Groups[1].Value : null; p.Plain = $"{p.App ?? "An app"} stopped responding"; }
        else if (s.Contains("WHEA", StringComparison.OrdinalIgnoreCase)) p.Plain = "Windows logged a hardware error (CPU, memory or PCIe)";
        else if (s.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && p.Id == 41) p.Plain = "The PC restarted without shutting down cleanly";
        else if (s.Equals("disk", StringComparison.OrdinalIgnoreCase) || s.Equals("Ntfs", StringComparison.OrdinalIgnoreCase) || s.Equals("stornvme", StringComparison.OrdinalIgnoreCase)) p.Plain = "A disk reported a problem";
        else if (s.Equals("Service Control Manager", StringComparison.OrdinalIgnoreCase)) p.Plain = "A Windows service failed or could not start";
        else if (s.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase)) p.Plain = "Windows recorded a program failure";
        else if (s.Contains("DistributedCOM", StringComparison.OrdinalIgnoreCase)) p.Plain = "A background Windows component reported an error (often harmless)";
        else if (s.Contains("Display", StringComparison.OrdinalIgnoreCase) || s.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase)) p.Plain = "The graphics driver reported a problem";
        else p.Plain = $"Error from {s}";
    }
}
