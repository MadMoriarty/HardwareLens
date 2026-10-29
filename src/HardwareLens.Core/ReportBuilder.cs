using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace HardwareLens.Core;

public static class ReportBuilder
{
    static readonly CultureInfo C = CultureInfo.InvariantCulture;
    public const string SchemaVersion = "1.0";

    public static readonly Dictionary<string, string> Units = new()
    {
        ["capacity"] = "bytes (JSON). Drives are displayed as vendor-style decimal size (1 TB = 10^12 bytes) with the Windows-style binary size (GiB = 2^30 bytes) in parentheses.",
        ["memory"] = "bytes; displayed in KiB/MiB/GiB (powers of 1024)",
        ["memorySpeed"] = "MT/s (mega-transfers per second), SMBIOS values",
        ["cpuPercent"] = "percent; 100 = all logical processors fully busy",
        ["diskThroughput"] = "bytes per second; displayed in MiB/s (2^20 bytes)",
        ["networkThroughput"] = "bytes per second; displayed in Mbit/s (10^6 bits). Link speeds are decimal bit rates.",
        ["latency"] = "milliseconds (average response time per I/O in the sample interval)",
        ["time"] = "ISO 8601 with local UTC offset"
    };

    // ---------- JSON ----------
    public static string Json(ReportData r)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        opts.Converters.Add(new JsonStringEnumConverter());
        var inv = r.Inventory;
        var doc = new Dictionary<string, object?>
        {
            ["schemaVersion"] = SchemaVersion,
            ["application"] = new { name = "HardwareLens", version = r.AppVersion, note = "Generated locally. Nothing is uploaded or sent anywhere." },
            ["reportGenerated"] = r.GeneratedAt.ToString("o", C),
            ["inventoryCollected"] = inv.CollectedAt.ToString("o", C),
            ["collectedAs"] = inv.Elevation,
            ["identifiersIncluded"] = r.IncludeIdentifiers,
            ["identifiersNote"] = r.IncludeIdentifiers ? "Serial numbers, MAC addresses and the computer name are included in 'identifiers'. Do not share this file publicly." : "Serial numbers, MAC addresses and the computer name were deliberately withheld.",
            ["units"] = Units,
            ["inventory"] = inv,
            ["live"] = r.Live == null ? null : new
            {
                collectedSeconds = r.Live.CollectedSeconds, refreshSeconds = r.Live.RefreshSeconds, logicalProcessors = r.Live.LogicalProcessors,
                note = "null values mean the reading was unavailable; they are never zero-filled. History arrays are downsampled to at most 240 points.",
                metrics = r.Live.Metrics,
                topProcessesByCpu = r.Live.TopProcesses.Select(p => new { p.Pid, p.Name, cpuPercentOfAllThreads = p.CpuPercent, workingSetBytes = p.WorkingSetBytes, privateWorkingSetBytes = p.PrivateWorkingSetBytes, commitBytes = p.CommitBytes, generalIoReadBytesPerSec = p.IoReadBps, generalIoWriteBytesPerSec = p.IoWriteBps, gpuEnginePercent = p.GpuPercent }),
                history = r.Live.Charts.Select(c => new { c.Title, unit = c.Unit, scaleToUnit = c.Scale, series = c.Series.Select(s => new { s.Name, points = Down(s.Data, 240) }) }),
                sourceProblems = r.Live.SourceProblems
            },
            ["observations"] = r.Observations,
            ["sources"] = inv.Sources,
            ["unavailableFields"] = inv.Unavailable,
            ["limitations"] = Limitations.All,
        };
        if (r.IncludeIdentifiers) doc["identifiers"] = inv.Identifiers;
        return JsonSerializer.Serialize(doc, opts);
    }

    static object Down(SeriesData d, int max)
    {
        int n = d.Count; if (n == 0) return Array.Empty<object>();
        int step = Math.Max(1, (int)Math.Ceiling(n / (double)max));
        var list = new List<object>();
        for (int i = 0; i < n; i += step)
        {
            // for each bucket report the maximum so short spikes are not lost; a NaN-only bucket stays null
            double best = double.NaN; for (int j = i; j < Math.Min(n, i + step); j++) if (!double.IsNaN(d.Values[j]) && (double.IsNaN(best) || d.Values[j] > best)) best = d.Values[j];
            list.Add(new { t = new DateTimeOffset(d.Ticks[i], TimeSpan.Zero).ToLocalTime().ToString("o", C), v = double.IsNaN(best) ? (double?)null : Math.Round(best, 3) });
        }
        return list;
    }

    // ---------- HTML ----------
    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    static string Or(string? s, string fallback = "Not reported by Windows") => string.IsNullOrWhiteSpace(s) ? "<span class=na>" + fallback + "</span>" : E(s);
    static string NA(string t = "Not reported by Windows") => "<span class=na>" + t + "</span>";

    public static string Html(ReportData r)
    {
        var inv = r.Inventory; var sb = new StringBuilder(32 * 1024);
        sb.Append("<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\"><title>HardwareLens report</title><style>").Append(Css).Append("</style></head><body><main>");
        sb.Append("<header><div><h1>HardwareLens report</h1><p class=sub>Hardware inventory and performance snapshot · generated ").Append(E(r.GeneratedAt.ToString("f", C))).Append(" (UTC").Append(r.GeneratedAt.ToString("zzz", C)).Append(")</p></div><span class=chip>Local-only · no telemetry</span></header>");
        sb.Append("<div class=note><b>Collection:</b> inventory collected ").Append(E(inv.CollectedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", C))).Append(" in ").Append(inv.CollectionSeconds.ToString("0.0", C)).Append(" s, running as <b>").Append(E(inv.Elevation)).Append("</b>. ");
        sb.Append(r.IncludeIdentifiers ? "<b class=warn>Identifiers (serial numbers, MAC addresses, computer name) are INCLUDED. Do not share this report publicly.</b>" : "Serial numbers, MAC addresses and the computer name are withheld.");
        sb.Append("</div>");

        // System
        var s = inv.System;
        sb.Append("<section><h2>System</h2>"); KV(sb,
            ("Computer", Join(s.Manufacturer, s.Model)), ("Form factor", s.FormFactor), ("Windows", Join(s.OsName, s.OsEdition)),
            ("Version / build", Join(s.OsVersion, s.OsBuild)), ("Architecture", s.OsArchitecture),
            ("Uptime", s.UptimeSeconds is double u ? Fmt.Duration(u) + (s.LastBoot is { } lb ? " (since " + lb.ToString("yyyy-MM-dd HH:mm", C) + ")" : "") : null),
            ("Usable RAM reported to Windows", s.UsableMemoryBytes is ulong ub ? Fmt.Bytes(ub) : null));
        sb.Append("</section>");

        // CPU / board / bios
        sb.Append("<section><h2>Processor, motherboard and firmware</h2>");
        foreach (var c in inv.Cpus) KV(sb, ("Processor", c.Name), ("Manufacturer", c.Manufacturer), ("Cores / threads", c.Cores != null ? $"{c.Cores} cores / {c.LogicalProcessors} threads" : null), ("Max clock", c.MaxClockMHz is int mh ? Fmt.Mhz(mh) : null), ("Socket", c.Socket), ("Cache", c.L2CacheKB != null || c.L3CacheKB != null ? $"L2 {Kb(c.L2CacheKB)}, L3 {Kb(c.L3CacheKB)}" : null));
        KV(sb, ("Motherboard", Join(inv.Board.Manufacturer, inv.Board.Model)), ("Board version", inv.Board.Version), ("BIOS vendor", inv.Bios.Vendor), ("BIOS version", inv.Bios.Version), ("BIOS date", inv.Bios.ReleaseDate?.ToString("yyyy-MM-dd", C)), ("SMBIOS version", inv.Bios.SmbiosVersion));
        sb.Append("</section>");

        // Memory
        var m = inv.Memory;
        sb.Append("<section><h2>Memory (RAM)</h2>");
        KV(sb, ("Installed (sum of modules)", m.TotalModuleBytes is ulong tm ? Fmt.Bytes(tm) : null), ("Slots", m.SlotsTotal != null ? $"{m.SlotsPopulated} of {m.SlotsTotal} populated" : $"{m.SlotsPopulated} populated (slot count not reported)"), ("Maximum supported", m.MaxCapacityBytes is ulong mx ? Fmt.Bytes(mx) : null), ("Type", m.Type));
        if (m.Modules.Count > 0)
        {
            sb.Append("<table><thead><tr><th>Slot</th><th>Manufacturer</th><th>Part number</th><th class=n>Capacity</th><th class=n>Configured speed</th><th class=n>Rated speed</th><th>Type</th><th>Form factor</th></tr></thead><tbody>");
            foreach (var x in m.Modules)
                sb.Append("<tr><td>").Append(Or(Join(x.Slot, x.Bank), "—")).Append("</td><td>").Append(Or(x.Manufacturer)).Append(x.ManufacturerNote != null ? " <small class=na>" + E(x.ManufacturerNote) + "</small>" : "").Append("</td><td>").Append(Or(x.PartNumber)).Append("</td><td class=n>").Append(x.CapacityBytes is ulong cb ? Fmt.Bytes(cb) : NA("—")).Append("</td><td class=n>").Append(x.ConfiguredSpeedMTs != null ? x.ConfiguredSpeedMTs + " MT/s" : NA("—")).Append("</td><td class=n>").Append(x.RatedSpeedMTs != null ? x.RatedSpeedMTs + " MT/s" : NA("—")).Append("</td><td>").Append(Or(x.Type, "—")).Append("</td><td>").Append(Or(x.FormFactor, "—")).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // GPU
        sb.Append("<section><h2>Graphics</h2>");
        if (inv.Gpus.Count == 0) sb.Append("<p class=na>No graphics adapters were reported.</p>");
        foreach (var g in inv.Gpus)
        {
            KV(sb, ("Adapter", g.Name), ("Vendor", g.Vendor), ("Type", g.Architecture + (g.ArchitectureNote != null ? " — " + g.ArchitectureNote : "")),
                ("Dedicated video memory (VRAM)", g.DedicatedVramBytes is ulong d ? Fmt.Bytes(d) : null),
                ("Shared system memory available to GPU", g.SharedMemoryBytes is ulong sh ? Fmt.Bytes(sh) + " (a pool of system RAM, not separate video memory)" : null),
                ("Driver", Join(g.DriverVersion, g.DriverDate?.ToString("yyyy-MM-dd", C))), ("PCI vendor / device ID", Join(g.PciVendorId, g.PciDeviceId, " / ")));
            sb.Append("<hr>");
        }
        if (inv.HiddenSoftwareAdapters > 0) sb.Append("<p class=na>").Append(inv.HiddenSoftwareAdapters).Append(" software/virtual display adapter(s) hidden (e.g. Microsoft Basic Render Driver).</p>");
        sb.Append("</section>");

        // Storage
        sb.Append("<section><h2>Storage</h2>");
        sb.Append("<p class=note>Windows health status below is the basic flag Windows reports. It is <b>not</b> a SMART test, surface scan or comprehensive diagnostic.</p>");
        foreach (var d in inv.Disks)
        {
            sb.Append("<h3>Disk ").Append(d.Number).Append(" — ").Append(E(d.Name)).Append("</h3>");
            KV(sb, ("Model / manufacturer", Join(d.Model, d.Manufacturer, " / ")), ("Media type", d.MediaType), ("Connection", d.Connection + (d.IsExternalOrRemovable ? " (external / removable)" : "")), ("Capacity", d.MediaPresent ? Fmt.Capacity(d.SizeBytes) : "No media"), ("Partition style", d.PartitionStyle), ("Firmware", d.Firmware), ("Spindle speed", d.SpindleRpm is int rpm && rpm > 0 ? rpm + " RPM" : null), ("Windows health status (basic)", d.WindowsHealthStatus), ("Operational status", d.OperationalStatus), ("Temperature", d.TemperatureC is int tc ? tc + " °C" : null), ("SSD life used (wear)", d.WearPercentUsed is int wp ? wp + "%" : null), ("Power-on hours", d.PowerOnHours is uint ph ? ph.ToString("N0", C) : null), ("Read / write errors (lifetime)", d.ReadErrorsTotal is uint re ? re + " / " + (d.WriteErrorsTotal?.ToString() ?? "—") : null));
            if (d.Volumes.Count > 0)
            {
                sb.Append("<table><thead><tr><th>Drive</th><th>Label</th><th>File system</th><th class=n>Size</th><th class=n>Free</th><th class=n>Free %</th></tr></thead><tbody>");
                foreach (var v in d.Volumes)
                    sb.Append("<tr><td>").Append(E(v.Letter)).Append("</td><td>").Append(Or(v.Label, "—")).Append("</td><td>").Append(Or(v.FileSystem, "—")).Append("</td><td class=n>").Append(Fmt.Bytes(v.SizeBytes)).Append("</td><td class=n>").Append(Fmt.Bytes(v.FreeBytes)).Append("</td><td class=n>").Append(v.SizeBytes > 0 ? (v.FreeBytes * 100.0 / v.SizeBytes).ToString("0.#", C) + "%" : "—").Append("</td></tr>");
                sb.Append("</tbody></table>");
            }
            else sb.Append("<p class=na>No volumes with a drive letter on this disk.</p>");
        }
        if (inv.HiddenEmptyReaders > 0) sb.Append("<p class=na>").Append(inv.HiddenEmptyReaders).Append(" empty card-reader slot(s) with no media hidden.</p>");
        sb.Append("</section>");

        // Network
        sb.Append("<section><h2>Network adapters</h2>");
        if (inv.Nics.Count == 0) sb.Append("<p class=na>No adapters reported.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>Adapter</th><th>Kind</th><th>Status</th><th class=n>Link speed (receive / transmit)</th><th>Physical</th></tr></thead><tbody>");
            foreach (var n in inv.Nics.OrderByDescending(n => n.IsPhysical).ThenBy(n => n.Name))
                sb.Append("<tr><td>").Append(E(n.Description)).Append(n.Name != n.Description ? " <small class=na>(" + E(n.Name) + ")</small>" : "").Append("</td><td>").Append(E(n.Kind)).Append("</td><td>").Append(E(n.Status)).Append("</td><td class=n>").Append(n.ReceiveLinkBps != null ? Fmt.LinkSpeed(n.ReceiveLinkBps) + " / " + Fmt.LinkSpeed(n.TransmitLinkBps) : NA("—")).Append("</td><td>").Append(n.IsVirtual ? "Virtual" : n.IsPhysical ? "Yes" : "No").Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // Battery
        sb.Append("<section><h2>Battery</h2>");
        if (inv.Battery is { } b) KV(sb, ("Battery", b.Name), ("Charge", b.ChargePercent != null ? b.ChargePercent + "%" : null), ("Status", b.Status), ("Chemistry", b.Chemistry), ("Design capacity", b.DesignCapacityMWh != null ? b.DesignCapacityMWh + " mWh" : null), ("Full-charge capacity", b.FullChargeCapacityMWh != null ? b.FullChargeCapacityMWh + " mWh" : null), ("Capacity lost to wear", b.WearPercent != null ? b.WearPercent.Value.ToString("0.#", C) + "%" : null), ("Charge cycles", b.CycleCount?.ToString(C)));
        else sb.Append("<p class=na>").Append(E(inv.BatteryNote ?? "No battery reported by Windows.")).Append("</p>");
        sb.Append("</section>");

        // Live
        if (r.Live is { } L)
        {
            sb.Append("<section><h2>Live performance (recent history)</h2><p class=note>Collected for ").Append(Fmt.Duration(L.CollectedSeconds)).Append(" at a ").Append(L.RefreshSeconds.ToString("0.#", C)).Append(" s refresh interval. CPU percentages: 100% = all ").Append(L.LogicalProcessors).Append(" logical processors fully busy. Gaps (sleep, paused collection) are left blank, not drawn as zero.</p>");
            sb.Append("<table><thead><tr><th>Metric</th><th class=n>Latest</th><th class=n>Average</th><th class=n>Peak</th><th class=n>Samples</th></tr></thead><tbody>");
            foreach (var mt in L.Metrics.Where(x => x.Samples > 0 || x.Key.Contains("gpu") == false))
                sb.Append("<tr><td>").Append(E(mt.Label)).Append("</td><td class=n>").Append(MV(mt, mt.Latest)).Append("</td><td class=n>").Append(MV(mt, mt.Average)).Append("</td><td class=n>").Append(MV(mt, mt.Max)).Append("</td><td class=n>").Append(mt.Samples == 0 ? NA("unavailable") : mt.Samples.ToString(C)).Append("</td></tr>");
            sb.Append("</tbody></table>");
            foreach (var ch in L.Charts) sb.Append(Svg(ch));
            if (L.TopProcesses.Count > 0)
            {
                sb.Append("<h3>Busiest processes at the last sample</h3><p class=note>Measured per process. Device totals above are separate measurements. I/O is all I/O the process requested (files, pipes, network), not physical disk traffic. Per-process network throughput is not measured.</p><table><thead><tr><th>Process</th><th class=n>PID</th><th class=n>CPU (of all threads)</th><th class=n>Working set</th><th class=n>Private working set</th><th class=n>General I/O read+write</th></tr></thead><tbody>");
                foreach (var p in L.TopProcesses)
                    sb.Append("<tr><td>").Append(E(p.Name)).Append("</td><td class=n>").Append(p.Pid).Append("</td><td class=n>").Append(Fmt.Percent(p.CpuPercent, 1)).Append("</td><td class=n>").Append(Fmt.Bytes(p.WorkingSetBytes)).Append("</td><td class=n>").Append(Fmt.Bytes(p.PrivateWorkingSetBytes)).Append("</td><td class=n>").Append(p.IoReadBps is double rd && p.IoWriteBps is double wr ? Fmt.Throughput(rd + wr) : "—").Append("</td></tr>");
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");
        }

        // Observations
        sb.Append("<section><h2>Observations</h2><p class=note>Plain-language results from fixed rules over measured windows. “Possible bottleneck” describes a condition that may limit performance; it is not a confirmed diagnosis. Brief spikes are not reported as problems.</p>");
        if (r.Observations.Count == 0) sb.Append("<p class=na>Live monitoring had not collected enough data when this report was created.</p>");
        foreach (var o in r.Observations)
        {
            sb.Append("<div class=\"obs ").Append(o.State.ToString().ToLowerInvariant()).Append("\"><div class=oh><b>").Append(E(o.Title)).Append("</b><span class=badge>").Append(E(o.StateLabel)).Append("</span></div><p>").Append(E(o.Measurement)).Append("</p><p class=sub>").Append(E(o.Duration)).Append("</p>");
            if (!string.IsNullOrEmpty(o.Explanation)) sb.Append("<p>").Append(E(o.Explanation)).Append("</p>");
            if (o.Details.Count > 0) { sb.Append("<ul>"); foreach (var dd in o.Details) sb.Append("<li>").Append(E(dd)).Append("</li>"); sb.Append("</ul>"); }
            if (!string.IsNullOrEmpty(o.Caveat)) sb.Append("<p class=cav>Does not prove: ").Append(E(o.Caveat)).Append("</p>");
            sb.Append("</div>");
        }
        sb.Append("</section>");

        // Identifiers
        if (r.IncludeIdentifiers)
        {
            sb.Append("<section><h2>Identifiers</h2><p class=warn>These values can identify this specific computer. Remove this section before sharing.</p>");
            if (inv.Identifiers.Count == 0) sb.Append("<p class=na>None were reported.</p>");
            else { sb.Append("<table><thead><tr><th>Item</th><th>Value</th></tr></thead><tbody>"); foreach (var kv in inv.Identifiers.OrderBy(k => k.Key)) sb.Append("<tr><td>").Append(E(kv.Key)).Append("</td><td>").Append(E(kv.Value)).Append("</td></tr>"); sb.Append("</tbody></table>"); }
            sb.Append("</section>");
        }

        // Sources / limits
        sb.Append("<section><h2>Data sources and unavailable fields</h2><table><thead><tr><th>Source</th><th>API</th><th>State</th><th>Note</th></tr></thead><tbody>");
        foreach (var src in inv.Sources) sb.Append("<tr><td>").Append(E(src.Name)).Append("</td><td>").Append(E(src.Api)).Append("</td><td>").Append(E(src.State)).Append("</td><td>").Append(Or(src.Note, "—")).Append("</td></tr>");
        sb.Append("</tbody></table>");
        if (inv.Unavailable.Count > 0)
        {
            sb.Append("<h3>Fields Windows did not provide</h3><table><thead><tr><th>Field</th><th>Reason</th></tr></thead><tbody>");
            foreach (var u2 in inv.Unavailable) sb.Append("<tr><td>").Append(E(u2.Field)).Append("</td><td>").Append(E(u2.Reason)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("<h3>Units</h3><ul>"); foreach (var kv in Units) sb.Append("<li><b>").Append(E(kv.Key)).Append(":</b> ").Append(E(kv.Value)).Append("</li>"); sb.Append("</ul>");
        sb.Append("<h3>Limitations</h3><ul>"); foreach (var l in Limitations.All) sb.Append("<li>").Append(E(l)).Append("</li>"); sb.Append("</ul></section>");
        sb.Append("<footer>HardwareLens ").Append(E(r.AppVersion)).Append(" · generated locally · no data was uploaded. Open this file in any browser; it has no external dependencies.</footer></main></body></html>");
        return sb.ToString();
    }

    static string MV(MetricSummary m, double? v)
    {
        if (v is null) return NA("unavailable");
        return m.Unit switch
        {
            "percent" => Fmt.Percent(v, 1), "bytes" => Fmt.Bytes(v), "ms" => v.Value.ToString("0.0", C) + " ms", "MHz" => Fmt.Mhz(v),
            "bytes/s" => m.Key.StartsWith("net.", StringComparison.Ordinal) ? Fmt.Mbit(v) + " (" + Fmt.Throughput(v) + ")" : Fmt.Throughput(v),
            _ => v.Value.ToString("0.##", C)
        };
    }

    static string Kb(int? kb) => kb is null ? "—" : kb >= 1024 ? (kb.Value / 1024.0).ToString("0.#", C) + " MiB" : kb + " KiB";
    static string? Join(string? a, string? b, string sep = " ") => string.IsNullOrWhiteSpace(a) ? (string.IsNullOrWhiteSpace(b) ? null : b) : string.IsNullOrWhiteSpace(b) ? a : a + sep + b;

    static void KV(StringBuilder sb, params (string k, string? v)[] rows)
    {
        sb.Append("<dl>");
        foreach (var (k, v) in rows) sb.Append("<dt>").Append(E(k)).Append("</dt><dd>").Append(string.IsNullOrWhiteSpace(v) ? NA() : E(v)).Append("</dd>");
        sb.Append("</dl>");
    }

    public static string Svg(ChartSpec c)
    {
        const int W = 720, H = 170, L = 52, R = 10, T = 10, B = 24;
        var sb = new StringBuilder();
        sb.Append("<figure><figcaption>").Append(E(c.Title)).Append(" <small>(").Append(E(c.Unit)).Append(")</small></figcaption>");
        long t0 = long.MaxValue, t1 = long.MinValue; double max = 0;
        foreach (var s in c.Series) { for (int i = 0; i < s.Data.Count; i++) { t0 = Math.Min(t0, s.Data.Ticks[i]); t1 = Math.Max(t1, s.Data.Ticks[i]); if (!double.IsNaN(s.Data.Values[i])) max = Math.Max(max, s.Data.Values[i] * c.Scale); } }
        if (t1 <= t0) { sb.Append("<p class=na>No data collected for this chart.</p></figure>"); return sb.ToString(); }
        double ymax = c.FixedMax ?? NiceMax(max);
        sb.Append("<svg viewBox=\"0 0 ").Append(W).Append(' ').Append(H).Append("\" role=img aria-label=\"").Append(E(c.Title)).Append(" history\">");
        for (int g = 0; g <= 4; g++)
        {
            double y = T + (H - T - B) * g / 4.0; double val = ymax * (4 - g) / 4.0;
            sb.Append("<line x1=").Append(L).Append(" x2=").Append(W - R).Append(" y1=").Append(y.ToString("0.#", C)).Append(" y2=").Append(y.ToString("0.#", C)).Append(" class=g /><text x=").Append(L - 6).Append(" y=").Append((y + 4).ToString("0.#", C)).Append(" class=t text-anchor=end>").Append(val >= 100 ? val.ToString("0", C) : val.ToString("0.#", C)).Append("</text>");
        }
        double span = t1 - t0;
        foreach (var s in c.Series)
        {
            var pts = new StringBuilder(); bool open = false; int step = Math.Max(1, s.Data.Count / 360);
            for (int i = 0; i < s.Data.Count; i += step)
            {
                double v = s.Data.Values[i];
                if (double.IsNaN(v)) { if (open) { sb.Append("<polyline fill=none stroke=\"").Append(s.Color).Append("\" stroke-width=1.6 points=\"").Append(pts).Append("\"/>"); pts.Clear(); open = false; } continue; }
                double x = L + (W - L - R) * ((s.Data.Ticks[i] - t0) / span); double y = T + (H - T - B) * (1 - Math.Min(1, v * c.Scale / ymax));
                pts.Append(x.ToString("0.#", C)).Append(',').Append(y.ToString("0.#", C)).Append(' '); open = true;
            }
            if (open) sb.Append("<polyline fill=none stroke=\"").Append(s.Color).Append("\" stroke-width=1.6 points=\"").Append(pts).Append("\"/>");
        }
        var start = new DateTimeOffset(t0, TimeSpan.Zero).ToLocalTime(); var end = new DateTimeOffset(t1, TimeSpan.Zero).ToLocalTime();
        sb.Append("<text x=").Append(L).Append(" y=").Append(H - 6).Append(" class=t>").Append(start.ToString("HH:mm:ss", C)).Append("</text><text x=").Append(W - R).Append(" y=").Append(H - 6).Append(" class=t text-anchor=end>").Append(end.ToString("HH:mm:ss", C)).Append("</text></svg><div class=legend>");
        foreach (var s in c.Series) sb.Append("<span><i style=\"background:").Append(s.Color).Append("\"></i>").Append(E(s.Name)).Append("</span>");
        sb.Append("</div></figure>");
        return sb.ToString();
    }

    public static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v))); double n = v / mag;
        double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return nice * mag;
    }

    const string Css = ":root{color-scheme:dark light;--bg:#15171c;--panel:#1b1d23;--line:#2c2f38;--tx:#e6e8ee;--mut:#8e929f;--acc:#ff6b57;--ok:#3fd0a0;--warn:#dca16a}" +
        "*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--tx);font:13px/1.45 'Segoe UI',system-ui,sans-serif}main{max-width:980px;margin:0 auto;padding:20px 16px 40px}" +
        "header{display:flex;justify-content:space-between;align-items:flex-start;gap:12px;border-bottom:2px solid var(--acc);padding-bottom:10px;margin-bottom:12px}h1{font-size:22px;margin:0}h2{font-size:15px;margin:0 0 8px;letter-spacing:.2px}h3{font-size:13px;margin:14px 0 6px}" +
        ".sub{color:var(--mut);margin:2px 0}.chip{border:1px solid var(--line);border-radius:999px;padding:2px 10px;color:var(--mut);font-size:11px;white-space:nowrap}" +
        "section{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:12px 14px;margin:10px 0}.note{background:#20232b;border:1px solid var(--line);border-radius:4px;padding:6px 10px;color:var(--tx);margin:8px 0}" +
        "dl{display:grid;grid-template-columns:230px 1fr;gap:3px 12px;margin:6px 0}dt{color:var(--mut)}dd{margin:0}.na{color:var(--mut);font-style:italic}.warn{color:var(--warn);font-weight:600}" +
        "table{border-collapse:collapse;width:100%;margin:6px 0}th,td{text-align:left;padding:4px 8px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--mut);font-weight:600;font-size:11px;text-transform:uppercase;letter-spacing:.5px}.n{text-align:right;font-variant-numeric:tabular-nums}" +
        "hr{border:0;border-top:1px solid var(--line);margin:8px 0}small{font-size:11px}figure{margin:12px 0}figcaption{font-weight:600;margin-bottom:2px}svg{width:100%;height:auto;background:#17191e;border:1px solid var(--line);border-radius:4px}" +
        ".g{stroke:#2c2f38;stroke-width:1}.t{fill:#8e929f;font-size:10px}.legend{display:flex;flex-wrap:wrap;gap:4px 14px;margin-top:4px;color:var(--mut)}.legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:5px}" +
        ".obs{border:1px solid var(--line);border-left:4px solid var(--mut);border-radius:4px;padding:8px 12px;margin:8px 0}.obs.possible{border-left-color:var(--warn)}.obs.observed{border-left-color:var(--acc)}.obs.notobserved{border-left-color:var(--ok)}.oh{display:flex;justify-content:space-between;gap:8px}.badge{font-size:11px;border:1px solid var(--line);border-radius:999px;padding:0 8px;color:var(--mut)}.cav{color:var(--mut);font-size:12px}" +
        "footer{color:var(--mut);margin-top:16px;font-size:11px}ul{margin:4px 0 4px 18px;padding:0}" +
        "@media(prefers-color-scheme:light){:root{--bg:#fff;--panel:#fafafa;--line:#d9dbe1;--tx:#1b1d23;--mut:#5e6270}.note{background:#f1f2f5}svg{background:#fff}.g{stroke:#e3e5ea}}@media print{body{background:#fff;color:#000}section{break-inside:avoid}}@media(max-width:640px){dl{grid-template-columns:1fr}}";
}
