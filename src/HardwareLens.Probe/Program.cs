using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HardwareLens.Core;
using HardwareLens.Windows;

// Console self-test: exercises the real collectors on this machine and cross-checks them against independent Windows sources
// (CIM cmdlets, Get-Counter, Get-Process run in a separate PowerShell process). Writes a report; changes nothing on the system.
var C = CultureInfo.InvariantCulture;
string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "selftest-output");
Directory.CreateDirectory(outDir);
string psScript = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "independent-check.ps1");
var report = new StringBuilder(); int pass = 0, fail = 0, info = 0;
void Line(string s) { report.AppendLine(s); Console.WriteLine(s); }
void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; Line($"- [{(ok ? "PASS" : "FAIL")}] {name}" + (detail.Length > 0 ? " — " + detail : "")); }
void Info(string name, string detail) { info++; Line($"- [INFO] {name} — {detail}"); }
Line($"# HardwareLens self-test\nStarted {DateTimeOffset.Now:O} on {Environment.OSVersion}, {Environment.ProcessorCount} logical processors, elevated={new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}\n");

// ---------- 1. Inventory ----------
Line("## Inventory");
var sw = Stopwatch.StartNew(); var inv = InventoryCollector.Collect(); sw.Stop();
Info("inventory collection time", $"{sw.Elapsed.TotalSeconds:0.00} s");
foreach (var s in inv.Sources) Info("source " + s.Name, s.State + (s.Note != null ? ": " + s.Note : ""));
File.WriteAllText(Path.Combine(outDir, "inventory-default.json"), ReportBuilder.Json(new ReportData { Inventory = inv }));

// ---------- 2. Start independent reference + live collector ----------
Line("\n## Live collector and independent reference");
string psOut = Path.Combine(outDir, "ps-independent.json"); if (File.Exists(psOut)) File.Delete(psOut);
Process? ps = null;
try { ps = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{psScript}\" -Out \"{psOut}\" -Seconds 12") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }); }
catch (Exception e) { Check("start independent PowerShell reference", false, e.Message); }
await Task.Delay(4000); // PowerShell spends a few seconds on CIM before its counter window; align the windows roughly
var collector = new LiveCollector(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(20));
collector.SetInventory(inv);
var samples = new List<LiveSample>(); collector.SampleReady += s => { lock (samples) samples.Add(s); };
var self = Process.GetCurrentProcess(); var cpu0 = self.TotalProcessorTime; var wall0 = Stopwatch.StartNew();
collector.Start();
await Task.Delay(3000);
Process? child = null; try { child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 9 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true }); } catch { }
await Task.Delay(5000);
var latest = collector.Latest;
if (child != null)
{
    var pingProc = latest?.Processes.FirstOrDefault(p => p.Pid == child.Id);
    Check("new child process (cmd.exe) appears in process list within ~5 s", pingProc != null, pingProc != null ? $"PID {pingProc.Pid} {pingProc.Name}" : "not found");
}
// PDH wildcard instance refresh: add a wildcard query, start a process, see whether it appears without recreating the query
using (var pq = new PdhQuery())
{
    var cnt = pq.Add(@"\Process(*)\ID Process"); var list = new List<KeyValuePair<string, double>>();
    pq.Collect(); pq.Collect(); pq.Read(cnt, list, out _); var before = list.Select(k => (int)k.Value).ToHashSet();
    Process? c2 = null; try { c2 = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true }); } catch { }
    await Task.Delay(1500); pq.Collect(); pq.Read(cnt, list, out _); var after = list.Select(k => (int)k.Value).ToHashSet();
    Check("PDH wildcard counters pick up new instances without re-creating the query", c2 != null && after.Contains(c2.Id) && !before.Contains(c2.Id), $"instances before={before.Count}, after={after.Count}");
}
if (ps != null) { if (!ps.WaitForExit(60000)) { try { ps.Kill(); } catch { } Check("independent PowerShell finished", false, "timed out"); } else Info("independent PowerShell exit code", ps.ExitCode.ToString(C) + " " + (ps.StandardError.ReadToEnd().Trim() is { Length: > 0 } er ? er[..Math.Min(300, er.Length)] : "")); }
await Task.Delay(1000);
var cpu1 = self.TotalProcessorTime; self.Refresh();
double selfCpuPct = (cpu1 - cpu0).TotalSeconds / wall0.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
List<LiveSample> win; lock (samples) win = samples.Where(s => !s.IsBaseline).ToList();
Info("samples collected", $"{win.Count} in {wall0.Elapsed.TotalSeconds:0.0} s; average collection cost {collector.LastCollectMilliseconds:0.0} ms (last)");
Check("self-test process CPU stays low while sampling at 1 s (includes PowerShell wait/IO)", selfCpuPct < 5, $"{selfCpuPct:0.00}% of all threads; working set {self.WorkingSet64 / 1048576.0:0} MiB");
Check("no unexpected sample gaps", win.Count >= 10, $"{win.Count} samples");

// ---------- 3. Compare ----------
Line("\n## Comparison with independent Windows sources");
JsonElement? J = null;
try { if (File.Exists(psOut)) J = JsonDocument.Parse(File.ReadAllText(psOut, Encoding.UTF8).TrimStart('﻿')).RootElement; } catch (Exception e) { Check("read independent reference JSON", false, e.Message); }
if (J is not JsonElement r) { Check("independent reference available", false, "PowerShell output missing"); }
else
{
    string Norm(string? s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim().ToLowerInvariant();
    IEnumerable<JsonElement> Arr(string n) => r.TryGetProperty(n, out var a) ? (a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : new[] { a }) : Enumerable.Empty<JsonElement>();
    string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;
    double? Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    var pc = Arr("cpu").FirstOrDefault(); var ic = inv.Cpus.FirstOrDefault();
    Check("CPU name", Norm(Str(pc, "name")) == Norm(ic?.Name), $"{ic?.Name}");
    Check("CPU cores/threads", Num(pc, "cores") == ic?.Cores && Num(pc, "threads") == ic?.LogicalProcessors, $"{ic?.Cores}C/{ic?.LogicalProcessors}T vs {Num(pc, "cores")}C/{Num(pc, "threads")}T; collector logical={collector.LogicalProcessors}");
    var pb = r.GetProperty("board"); Check("Motherboard model", Norm(Str(pb, "model")) == Norm(inv.Board.Model), inv.Board.Manufacturer + " " + inv.Board.Model);
    var pbi = r.GetProperty("bios"); Check("BIOS version and date", Norm(Str(pbi, "version")) == Norm(inv.Bios.Version) && Str(pbi, "date") == inv.Bios.ReleaseDate?.ToString("yyyy-MM-dd"), $"{inv.Bios.Version} {inv.Bios.ReleaseDate:yyyy-MM-dd}");
    var pm = Arr("ramModules").ToList();
    Check("RAM module count", pm.Count == inv.Memory.Modules.Count, $"{inv.Memory.Modules.Count} modules");
    for (int i = 0; i < Math.Min(pm.Count, inv.Memory.Modules.Count); i++)
    {
        var a = pm[i]; var b = inv.Memory.Modules.FirstOrDefault(x => x.Slot == Str(a, "slot")) ?? inv.Memory.Modules[i];
        Check($"RAM module {b.Slot}: capacity, part number, configured speed (MT/s)", (ulong)(Num(a, "capacity") ?? 0) == b.CapacityBytes && Norm(Str(a, "part")) == Norm(b.PartNumber) && (int?)Num(a, "configured") == b.ConfiguredSpeedMTs, $"{Fmt.Bytes(b.CapacityBytes)} {b.PartNumber} {b.ConfiguredSpeedMTs} MT/s (rated {b.RatedSpeedMTs}), {b.Manufacturer}");
    }
    Info("RAM summary", $"{Fmt.Bytes(inv.Memory.TotalModuleBytes)} in {inv.Memory.SlotsPopulated}/{inv.Memory.SlotsTotal} slots, type {inv.Memory.Type}; Windows usable {Fmt.Bytes(inv.System.UsableMemoryBytes)}");
    var os = r.GetProperty("os"); Check("Windows build", (Str(os, "build") is string bld) && (inv.System.OsBuild ?? "").StartsWith(bld), $"{inv.System.OsName} {inv.System.OsVersion} build {inv.System.OsBuild}; uptime {Fmt.Duration(inv.System.UptimeSeconds ?? 0)}");
    foreach (var g in Arr("gpuRegistry"))
    {
        var mine = inv.Gpus.FirstOrDefault(x => Norm(x.Name) == Norm(Str(g, "name")));
        Check($"GPU '{Str(g, "name")}' present with dedicated VRAM equal to registry value", mine != null && mine.DedicatedVramBytes == (ulong?)Num(g, "vram"), mine == null ? "not found" : $"DXGI {Fmt.Bytes(mine.DedicatedVramBytes)} vs registry {Fmt.Bytes((double?)Num(g, "vram"))}; type {mine.Architecture}");
    }
    foreach (var g in inv.Gpus) Info($"GPU {g.Name}", $"{g.Architecture} ({g.ArchitectureNote}); dedicated {Fmt.Bytes(g.DedicatedVramBytes)}, shared pool {Fmt.Bytes(g.SharedMemoryBytes)}; driver {g.DriverVersion}; luid {g.LuidKey}");
    var pdisks = Arr("disks").ToList();
    foreach (var d in pdisks.Where(x => (Num(x, "size") ?? 0) > 0))
    {
        int n = (int)(Num(d, "number") ?? -1); var mine = inv.Disks.FirstOrDefault(x => x.Number == n);
        Check($"Disk {n} '{Str(d, "name")}': name, size, media, bus", mine != null && Norm(mine.Name) == Norm(Str(d, "name")) && mine.SizeBytes == (ulong)(Num(d, "size") ?? 0) && (Str(d, "media") == "Unspecified" ? mine.MediaType.StartsWith("Not reported") : Str(d, "media") == mine.MediaType) && Norm(Str(d, "bus")) == Norm(mine.Connection).Replace(" controller", "").Replace("raid", "raid").Replace("sd card", "sd"), mine == null ? "missing" : $"{mine.MediaType}, {mine.Connection}, {Fmt.Capacity(mine.SizeBytes)}, health {mine.WindowsHealthStatus} (PS: {Str(d, "media")}/{Str(d, "bus")}/{Str(d, "health")})");
    }
    int emptyPs = pdisks.Count(x => (Num(x, "size") ?? 0) == 0); Info("empty/zero-size physical disks hidden", $"PowerShell lists {emptyPs}; app hid {inv.HiddenEmptyReaders}");
    var pv = Arr("volumes").ToList(); var myVols = inv.Disks.SelectMany(d => d.Volumes).ToList();
    foreach (var v in pv)
    {
        var mine = myVols.FirstOrDefault(x => x.Letter == Str(v, "letter"));
        double sz = Num(v, "size") ?? 0, fr = Num(v, "free") ?? 0;
        Check($"Volume {Str(v, "letter")}: file system, size, free space (±1%)", mine != null && mine.FileSystem == Str(v, "fs") && Math.Abs(mine.SizeBytes - sz) < 1 && Math.Abs(mine.FreeBytes - fr) <= Math.Max(sz * 0.01, 50e6), mine == null ? "missing from disk volume lists" : $"{mine.FileSystem} {Fmt.Bytes(mine.SizeBytes)} free {Fmt.Bytes(mine.FreeBytes)} (PS free {Fmt.Bytes(fr)})");
    }
    foreach (var n in Arr("nics"))
    {
        var mine = inv.Nics.FirstOrDefault(x => x.Name == Str(n, "name"));
        bool st = mine != null && string.Equals(mine.Status, Str(n, "status"), StringComparison.OrdinalIgnoreCase);
        Check($"Network adapter '{Str(n, "name")}': status", st, mine == null ? "missing" : $"{mine.Kind}, {mine.Status}, link {Fmt.LinkSpeed(mine.ReceiveLinkBps)}/{Fmt.LinkSpeed(mine.TransmitLinkBps)} (PS: {Str(n, "linkSpeed")}), physical={mine.IsPhysical}, virtual={mine.IsVirtual}");
    }
    Info("battery", inv.Battery != null ? $"{inv.Battery.Name} {inv.Battery.ChargePercent}%" : inv.BatteryNote ?? "none");

    // ----- live values -----
    var counters = Arr("counters").ToList();
    double Mean(IEnumerable<double?> xs) { var l = xs.Where(x => x != null).Select(x => x!.Value).ToList(); return l.Count == 0 ? double.NaN : l.Average(); }
    double psCpu = Mean(counters.Select(c => Num(c, "cpu"))), appCpu = Mean(win.Select(s => s.CpuPercent));
    Check("CPU total % (mean over window) within 20 points of Get-Counter", Math.Abs(psCpu - appCpu) < 20, $"app {appCpu:0.0}% vs PowerShell {psCpu:0.0}% (windows only overlap in time)");
    double psAvail = Mean(counters.Select(c => Num(c, "availMB"))), appAvail = Mean(win.Select(s => s.Memory == null ? (double?)null : s.Memory.AvailableBytes / 1048576.0));
    Check("Available memory within 5% of \\Memory\\Available MBytes", Math.Abs(psAvail - appAvail) / Math.Max(psAvail, 1) < 0.05, $"app {appAvail:0} MiB vs PS {psAvail:0} MB");
    double psRx = Mean(counters.Select(c => Num(c, "netRx"))), appRx = Mean(win.Select(s => (double?)s.Nets.Where(n => n.InTotal).Sum(n => n.RxBps ?? 0)));
    Info("network receive (mean)", $"app total (connected physical) {Fmt.Mbit(appRx)}; PowerShell sum of ALL interface instances {Fmt.Mbit(psRx)}");
    double psDR = Mean(counters.Select(c => Num(c, "diskR"))), appDR = Mean(win.Select(s => (double?)s.Disks.Sum(d => d.ReadBps ?? 0)));
    double psDW = Mean(counters.Select(c => Num(c, "diskW"))), appDW = Mean(win.Select(s => (double?)s.Disks.Sum(d => d.WriteBps ?? 0)));
    Check("Disk read/write throughput within 8 MiB/s of Get-Counter _Total", Math.Abs(psDR - appDR) < 8 * 1048576 && Math.Abs(psDW - appDW) < 8 * 1048576, $"read app {Fmt.Throughput(appDR)} vs PS {Fmt.Throughput(psDR)}; write app {Fmt.Throughput(appDW)} vs PS {Fmt.Throughput(psDW)}");
    double psGpu = Mean(counters.Select(c => Num(c, "gpuMax"))), appGpu = Mean(win.Select(s => s.Gpus.Count == 0 ? (double?)null : s.Gpus.Max(g => g.EngineUtilPercent ?? 0)));
    if (!double.IsNaN(psGpu)) Check("GPU busiest-engine utilization within 20 points of Get-Counter", Math.Abs(psGpu - appGpu) < 20, $"app {appGpu:0.0}% vs PS {psGpu:0.0}%");
    else Info("GPU utilization", "Get-Counter returned no GPU engine data");
    double psDed = Mean(counters.Select(c => Num(c, "gpuDed"))), appDed = Mean(win.Select(s => s.Gpus.Count == 0 ? (double?)null : s.Gpus.Sum(g => g.DedicatedUsedBytes ?? 0)));
    if (!double.IsNaN(psDed)) Check("GPU dedicated memory in use within 5% of Get-Counter", Math.Abs(psDed - appDed) / Math.Max(psDed, 1) < 0.05 + 100e6 / Math.Max(psDed, 1), $"app {Fmt.Bytes(appDed)} vs PS {Fmt.Bytes(psDed)}");
    var lastProcs = collector.Latest?.Processes ?? new List<ProcessSample>(); int nProc = 0, okProc = 0;
    foreach (var p in Arr("processes").Where(x => Num(x, "cpuPct") != null).OrderByDescending(x => Num(x, "cpuPct")).Take(6))
    {
        var mine = lastProcs.FirstOrDefault(x => x.Pid == (int)(Num(p, "pid") ?? -1)); nProc++;
        bool good = mine != null && Math.Abs((double)mine.WorkingSetBytes - (Num(p, "workingSet") ?? 0)) < Math.Max(0.1 * mine.WorkingSetBytes, 20e6) && Math.Abs((double)mine.CommitBytes - (Num(p, "privateBytes") ?? 0)) < Math.Max(0.1 * mine.CommitBytes, 20e6);
        if (good) okProc++;
        Info($"process {Str(p, "name")} (PID {Num(p, "pid")})", mine == null ? "exited or not found" : $"working set {Fmt.Bytes(mine.WorkingSetBytes)} (PS {Fmt.Bytes(Num(p, "workingSet"))}); commit {Fmt.Bytes(mine.CommitBytes)} (PS private {Fmt.Bytes(Num(p, "privateBytes"))}); CPU now {mine.CpuPercent:0.0}% vs PS window avg {Num(p, "cpuPct")}%");
    }
    Check("per-process working set and private bytes agree with Get-Process (top PowerShell CPU users)", nProc > 0 && okProc >= nProc - 1, $"{okProc}/{nProc}");
    int psCount = Arr("processes").Count(); int myCount = lastProcs.Count + 1;
    Check("process count within 15% of Get-Process", Math.Abs(psCount - myCount) <= Math.Max(8, psCount * 0.15), $"app {myCount} (incl. idle) vs Get-Process {psCount}");
}

// ---------- 4. Observations / export / recording ----------
Line("\n## Observations, export and recording");
var obs = collector.Observations; Check("observations produced", obs.Count > 0, $"{obs.Count} observations");
foreach (var o in obs) Info($"observation: {o.Title}", $"{o.StateLabel} — {o.Measurement} [{o.Duration}]");
Check("no observation claims a bottleneck after only seconds of data", obs.All(o => o.State != ObservationState.Possible || o.Id == "process"));
var live = LiveSummaryBuilder.Build(collector.Store, DateTimeOffset.Now, collector.Latest, 1, collector.LogicalProcessors, collector.Health);
var data = new ReportData { Inventory = inv, Live = live, Observations = obs.ToList() };
string htmlDefault = ReportBuilder.Html(data), jsonDefault = ReportBuilder.Json(data);
data.IncludeIdentifiers = true; string htmlIds = ReportBuilder.Html(data), jsonIds = ReportBuilder.Json(data); data.IncludeIdentifiers = false;
File.WriteAllText(Path.Combine(outDir, "report-default.html"), htmlDefault); File.WriteAllText(Path.Combine(outDir, "report-default.json"), jsonDefault);
File.WriteAllText(Path.Combine(outDir, "report-with-identifiers.html"), htmlIds); File.WriteAllText(Path.Combine(outDir, "report-with-identifiers.json"), jsonIds);
var ids = inv.Identifiers.Values.Where(v => v.Length >= 5).Distinct().ToList();
Info("identifier values collected in memory", $"{ids.Count} (not printed)");
bool leaked = ids.Any(v => htmlDefault.Contains(v, StringComparison.OrdinalIgnoreCase) || jsonDefault.Contains(v, StringComparison.OrdinalIgnoreCase));
Check("default HTML and JSON contain none of the serials, MAC addresses or the computer name", !leaked && !htmlDefault.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && !jsonDefault.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase));
Check("identifier option includes them in both formats", ids.Count == 0 || (ids.Any(v => htmlIds.Contains(v, StringComparison.OrdinalIgnoreCase)) && ids.Any(v => jsonIds.Contains(v, StringComparison.OrdinalIgnoreCase))));
Check("JSON parses and has units, sources and limitations", JsonDocument.Parse(jsonDefault).RootElement is { } je && je.TryGetProperty("units", out _) && je.TryGetProperty("sources", out _) && je.TryGetProperty("limitations", out _));
Check("HTML report is self-contained (no external URLs)", !System.Text.RegularExpressions.Regex.IsMatch(htmlDefault, @"(src|href)\s*=\s*[""']?https?://"));
string recDir = Path.Combine(outDir, "recordings"); var rec = collector.StartRecording(recDir, 20_000);
await Task.Delay(8000);
Check("recording stops at the size cap instead of growing", !rec.Active && rec.BytesWritten <= 22_000, $"stopped='{rec.StoppedReason}', {rec.BytesWritten} bytes, {rec.Rows} rows");
Info("recording files", string.Join(", ", Directory.GetFiles(recDir).Select(f => Path.GetFileName(f) + " " + new FileInfo(f).Length + " B")));
// suspend / resume semantics
collector.Suspended = true; await Task.Delay(4500); collector.Suspended = false; await Task.Delay(5000);
var cpuSeries = collector.Store.Get(SeriesKeys.Cpu, DateTimeOffset.Now, TimeSpan.FromMinutes(5));
Check("suspended collection leaves an explicit gap (NaN), not zeros", cpuSeries.Values.Any(double.IsNaN) && cpuSeries.Values.Where(v => !double.IsNaN(v)).Any(), $"{cpuSeries.Count} points, {cpuSeries.Values.Count(double.IsNaN)} gap marker(s)");
collector.Dispose();

Line($"\n## Result\n{pass} passed, {fail} failed, {info} informational.");
File.WriteAllText(Path.Combine(outDir, "selftest-report.md"), report.ToString());
return fail == 0 ? 0 : 1;
