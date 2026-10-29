using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using HardwareLens.Core;
using Microsoft.Win32;
namespace HardwareLens.Windows;

/// <summary>Collects the hardware inventory from documented Windows interfaces (CIM/WMI, DXGI, registry). Values Windows does not provide stay null and are listed as unavailable; nothing is guessed.</summary>
public static class InventoryCollector
{
    const string Cimv2 = @"root\cimv2", Storage = @"root\Microsoft\Windows\Storage", StdCim = @"root\StandardCimv2", RootWmi = @"root\wmi";
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static Inventory Collect()
    {
        var sw = Stopwatch.StartNew();
        var inv = new Inventory { CollectedAt = DateTimeOffset.Now };
        try { using var id = WindowsIdentity.GetCurrent(); inv.Elevation = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator) ? "Administrator (elevated)" : "Standard user"; } catch { inv.Elevation = "Unknown"; }

        var gate = new object();
        void Section(string name, string api, Action<Inventory, Action<string?>> body)
        {
            string? note = null;
            try { body(inv, n => note = n); lock (gate) inv.Sources.Add(new SourceStatus(name, api, note == null ? "ok" : "partial", note)); }
            catch (Exception e) { lock (gate) inv.Sources.Add(new SourceStatus(name, api, "unavailable", e.GetType().Name + ": " + e.Message)); lock (gate) inv.Unavailable.Add(new UnavailableField(name, "Source failed: " + e.Message)); }
        }
        var tasks = new List<Task>
        {
            Task.Run(() => Section("System and Windows", "CIM Win32_ComputerSystem / Win32_OperatingSystem / Win32_SystemEnclosure; registry CurrentVersion", Sys)),
            Task.Run(() => Section("Processor", "CIM Win32_Processor", Cpu)),
            Task.Run(() => Section("Motherboard and BIOS", "CIM Win32_BaseBoard / Win32_BIOS", BoardBios)),
            Task.Run(() => Section("Memory modules", "CIM Win32_PhysicalMemory / Win32_PhysicalMemoryArray (SMBIOS)", Mem)),
            Task.Run(() => Section("Graphics", "DXGI + Direct3D 12 architecture query; CIM Win32_VideoController", Gpu)),
            Task.Run(() => Section("Storage", "CIM MSFT_PhysicalDisk / MSFT_Disk / MSFT_Partition / Win32_DiskDrive / Win32_LogicalDisk", Disks)),
            Task.Run(() => Section("Network adapters", "CIM MSFT_NetAdapter (Win32_NetworkAdapter fallback)", Nics)),
            Task.Run(() => Section("Battery", "CIM Win32_Battery; root\\wmi Battery* classes", Battery)),
        };
        Task.WaitAll(tasks.ToArray());
        inv.Sources.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        inv.CollectionSeconds = sw.Elapsed.TotalSeconds;
        return inv;
    }

    static void Miss(Inventory inv, string field, string reason) { lock (inv.Unavailable) inv.Unavailable.Add(new UnavailableField(field, reason)); }
    static void Id(Inventory inv, string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) lock (inv.Identifiers) inv.Identifiers[key] = value.Trim(); }
    static string Clean(string? s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
    static bool IsGeneric(string? s) => string.IsNullOrWhiteSpace(s) || new[] { "to be filled by o.e.m.", "default string", "system manufacturer", "system product name", "not specified", "unknown", "none", "o.e.m.", "(standard disk drives)", "ata", "nvme", "generic", "usb", "scsi" }.Contains(s.Trim().ToLowerInvariant());
    static string? Real(string? s) => IsGeneric(s) ? null : Clean(s);

    // ---------------- System ----------------
    static void Sys(Inventory inv, Action<string?> partial)
    {
        var cs = Wmi.Query(Cimv2, "SELECT * FROM Win32_ComputerSystem").FirstOrDefault();
        var os = Wmi.Query(Cimv2, "SELECT * FROM Win32_OperatingSystem").FirstOrDefault();
        var enc = Wmi.Query(Cimv2, "SELECT * FROM Win32_SystemEnclosure").FirstOrDefault();
        var s = inv.System;
        if (cs != null)
        {
            s.Manufacturer = Real(cs.Str("Manufacturer")); s.Model = Real(cs.Str("Model")); s.UsableMemoryBytes = cs.U64("TotalPhysicalMemory");
            Id(inv, "System: computer name", cs.Str("Name") ?? Environment.MachineName);
        }
        if (s.Manufacturer == null) Miss(inv, "system.manufacturer", "Not provided by firmware (blank or placeholder such as 'System manufacturer').");
        if (s.Model == null) Miss(inv, "system.model", "Not provided by firmware (blank or placeholder such as 'System Product Name').");
        if (os != null)
        {
            s.OsName = Clean(os.Str("Caption")); s.OsArchitecture = os.Str("OSArchitecture");
            s.OsBuild = os.Str("BuildNumber");
            if (os.Date("LastBootUpTime") is DateTime lb) { s.LastBoot = new DateTimeOffset(lb); s.UptimeSeconds = Math.Max(0, (DateTimeOffset.Now - s.LastBoot.Value).TotalSeconds); }
        }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k != null)
            {
                s.OsEdition = k.GetValue("EditionID") as string;
                string? dv = k.GetValue("DisplayVersion") as string ?? k.GetValue("ReleaseId") as string;
                s.OsVersion = dv;
                if (k.GetValue("UBR") is int ubr && s.OsBuild != null) s.OsBuild = s.OsBuild + "." + ubr.ToString(C);
            }
        }
        catch (Exception e) { partial("Registry version details unavailable: " + e.Message); }
        if (s.OsVersion == null) Miss(inv, "system.osVersion", "DisplayVersion not present in registry.");
        var ct = enc?.U16Array("ChassisTypes");
        if (ct is { Length: > 0 })
        {
            var map = new Dictionary<int, string> { [3] = "Desktop", [4] = "Low-profile desktop", [5] = "Pizza box", [6] = "Mini tower", [7] = "Tower", [8] = "Portable", [9] = "Laptop", [10] = "Notebook", [11] = "Handheld", [12] = "Docking station", [13] = "All-in-one", [14] = "Sub-notebook", [15] = "Space-saving", [16] = "Lunch box", [17] = "Server", [23] = "Rack-mount server", [24] = "Sealed-case PC", [30] = "Tablet", [31] = "Convertible", [32] = "Detachable", [35] = "Mini PC", [36] = "Stick PC" };
            s.FormFactor = map.TryGetValue(ct[0], out var f) ? f + " (SMBIOS chassis type " + ct[0] + ")" : "SMBIOS chassis type " + ct[0];
        }
        else Miss(inv, "system.formFactor", "Chassis type not reported.");
    }

    // ---------------- CPU ----------------
    static void Cpu(Inventory inv, Action<string?> partial)
    {
        foreach (var r in Wmi.Query(Cimv2, "SELECT * FROM Win32_Processor"))
            inv.Cpus.Add(new CpuInfo { Name = Clean(r.Str("Name")), Manufacturer = r.Str("Manufacturer"), Cores = r.Int("NumberOfCores"), LogicalProcessors = r.Int("NumberOfLogicalProcessors"), MaxClockMHz = r.Int("MaxClockSpeed"), Socket = r.Str("SocketDesignation"), L2CacheKB = r.Int("L2CacheSize"), L3CacheKB = r.Int("L3CacheSize") });
        if (inv.Cpus.Count == 0) throw new InvalidOperationException("Win32_Processor returned no rows.");
    }

    // ---------------- Board / BIOS ----------------
    static void BoardBios(Inventory inv, Action<string?> partial)
    {
        var bb = Wmi.Query(Cimv2, "SELECT * FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Wmi.Query(Cimv2, "SELECT * FROM Win32_BIOS").FirstOrDefault();
        if (bb != null) { inv.Board.Manufacturer = Real(bb.Str("Manufacturer")); inv.Board.Model = Real(bb.Str("Product")); inv.Board.Version = Real(bb.Str("Version")); Id(inv, "Motherboard serial number", bb.Str("SerialNumber")); }
        if (inv.Board.Model == null) Miss(inv, "board.model", "Not provided by firmware.");
        if (bios != null)
        {
            inv.Bios.Vendor = Real(bios.Str("Manufacturer")); inv.Bios.Version = Clean(bios.Str("SMBIOSBIOSVersion") ?? bios.Str("Version")); inv.Bios.ReleaseDate = bios.Date("ReleaseDate");
            if (bios.Int("SMBIOSMajorVersion") is int ma) inv.Bios.SmbiosVersion = ma + "." + (bios.Int("SMBIOSMinorVersion") ?? 0);
            Id(inv, "BIOS serial number", bios.Str("SerialNumber"));
        }
        if (inv.Bios.ReleaseDate == null) Miss(inv, "bios.releaseDate", "Not reported.");
    }

    // ---------------- Memory ----------------
    static readonly Dictionary<int, string> MemType = new() { [20] = "DDR", [21] = "DDR2", [22] = "DDR2 FB-DIMM", [24] = "DDR3", [26] = "DDR4", [27] = "LPDDR", [28] = "LPDDR2", [29] = "LPDDR3", [30] = "LPDDR4", [34] = "DDR5", [35] = "LPDDR5" };
    static void Mem(Inventory inv, Action<string?> partial)
    {
        var arr = Wmi.Query(Cimv2, "SELECT * FROM Win32_PhysicalMemoryArray").FirstOrDefault(a => a.Int("Use") is 3 or null) ?? Wmi.Query(Cimv2, "SELECT * FROM Win32_PhysicalMemoryArray").FirstOrDefault();
        var m = inv.Memory;
        if (arr != null)
        {
            m.SlotsTotal = arr.Int("MemoryDevices");
            ulong? maxKb = arr.U64("MaxCapacityEx") ?? arr.U64("MaxCapacity");
            if (maxKb is ulong kb && kb > 0) m.MaxCapacityBytes = kb * 1024;
        }
        foreach (var r in Wmi.Query(Cimv2, "SELECT * FROM Win32_PhysicalMemory"))
        {
            string? mfr = r.Str("Manufacturer"); string? note = null;
            if (mfr != null && System.Text.RegularExpressions.Regex.IsMatch(mfr, "^[0-9A-Fa-f]{4}$")) note = "JEDEC manufacturer ID; Windows did not provide a name";
            if (IsGeneric(mfr)) mfr = null;
            int? smType = r.Int("SMBIOSMemoryType");
            var mod = new MemoryModule
            {
                Slot = r.Str("DeviceLocator"), Bank = r.Str("BankLabel"), Manufacturer = mfr, ManufacturerNote = mfr != null ? note : null, PartNumber = Real(r.Str("PartNumber")),
                CapacityBytes = r.U64("Capacity"),
                ConfiguredSpeedMTs = r.Int("ConfiguredClockSpeed") is > 0 and int cs ? cs : null,
                RatedSpeedMTs = r.Int("Speed") is > 0 and int sp ? sp : null,
                Type = smType is int t && MemType.TryGetValue(t, out var tn) ? tn : null,
                FormFactor = r.Int("FormFactor") switch { 8 => "DIMM", 12 => "SO-DIMM", 7 => "SIMM", 11 => "RIMM", 13 => "SRIMM", 21 or 22 => "Soldered (BGA)", _ => null },
                ConfiguredVoltageMv = r.Int("ConfiguredVoltage") is > 0 and int cv ? cv : null,
            };
            Id(inv, $"RAM module {mod.Slot ?? mod.Bank} serial number", r.Str("SerialNumber"));
            m.Modules.Add(mod);
        }
        m.SlotsPopulated = m.Modules.Count;
        if (m.Modules.Count == 0) throw new InvalidOperationException("Win32_PhysicalMemory returned no modules (SMBIOS data may be unavailable, e.g. in some virtual machines).");
        m.TotalModuleBytes = (ulong)m.Modules.Sum(x => (decimal)(x.CapacityBytes ?? 0));
        m.Type = m.Modules.Select(x => x.Type).FirstOrDefault(x => x != null);
        if (m.Type == null) Miss(inv, "memory.type", "SMBIOS memory type not reported.");
        if (m.Modules.Any(x => x.PartNumber == null)) Miss(inv, "memory.modules[].partNumber", "Some modules do not report a part number.");
        if (m.Modules.Any(x => x.ConfiguredSpeedMTs == null)) Miss(inv, "memory.modules[].configuredSpeedMTs", "Configured speed not reported for some modules.");
    }

    // ---------------- GPU ----------------
    static void Gpu(Inventory inv, Action<string?> partial)
    {
        var dxgi = DxgiProbe.Enumerate(out string? dxErr);
        List<Row> wmi = new();
        try { wmi = Wmi.Query(Cimv2, "SELECT * FROM Win32_VideoController"); } catch (Exception e) { partial("Driver details unavailable: " + e.Message); }
        string Ven(string? pnp) => System.Text.RegularExpressions.Regex.Match(pnp ?? "", "VEN_([0-9A-Fa-f]{4})").Groups[1].Value.ToUpperInvariant();
        string Dev(string? pnp) => System.Text.RegularExpressions.Regex.Match(pnp ?? "", "DEV_([0-9A-Fa-f]{4})").Groups[1].Value.ToUpperInvariant();
        var usedWmi = new HashSet<Row>();
        foreach (var d in dxgi)
        {
            if (d.IsSoftware) { inv.HiddenSoftwareAdapters++; continue; }
            var w = wmi.FirstOrDefault(x => !usedWmi.Contains(x) && Ven(x.Str("PNPDeviceID")) == d.VendorId.ToString("X4") && Dev(x.Str("PNPDeviceID")) == d.DeviceId.ToString("X4"));
            if (w != null) usedWmi.Add(w);
            var g = new GpuInfo
            {
                Name = Clean(w?.Str("Name") ?? d.Description), Vendor = w?.Str("AdapterCompatibility"), PciVendorId = "0x" + d.VendorId.ToString("X4"), PciDeviceId = "0x" + d.DeviceId.ToString("X4"),
                DriverVersion = w?.Str("DriverVersion"), DriverDate = w?.Date("DriverDate"),
                DedicatedVramBytes = d.DedicatedVideoMemory, SharedMemoryBytes = d.SharedSystemMemory, LuidKey = d.LuidKey,
                Architecture = d.IsUma is bool u ? (u ? "Integrated" : "Discrete") : "Unknown",
                ArchitectureNote = d.IsUma is bool u2 ? (u2 ? "unified memory architecture: shares system RAM" : "has its own dedicated video memory") : (d.Note ?? "Direct3D 12 did not report the memory architecture"),
            };
            if (w == null) Miss(inv, $"gpu[{g.Name}].driverVersion", "No matching Win32_VideoController entry.");
            inv.Gpus.Add(g);
        }
        // Adapters Windows lists that DXGI did not enumerate (remote/virtual display drivers): counted, not shown as GPUs.
        inv.HiddenSoftwareAdapters += wmi.Count(x => !usedWmi.Contains(x) && dxgi.Count > 0);
        if (dxgi.Count == 0)
        {
            partial("DXGI unavailable (" + (dxErr ?? "no adapters") + "); using Win32_VideoController only, so dedicated VRAM and integrated/discrete type are unknown.");
            foreach (var w in wmi)
                inv.Gpus.Add(new GpuInfo { Name = Clean(w.Str("Name")), Vendor = w.Str("AdapterCompatibility"), DriverVersion = w.Str("DriverVersion"), DriverDate = w.Date("DriverDate"), PciVendorId = Ven(w.Str("PNPDeviceID")) is { Length: > 0 } v ? "0x" + v : null, PciDeviceId = Dev(w.Str("PNPDeviceID")) is { Length: > 0 } dv ? "0x" + dv : null, Architecture = "Unknown", ArchitectureNote = "Memory architecture unavailable without DXGI" });
        }
        foreach (var g in inv.Gpus)
        {
            if (g.Architecture == "Unknown") Miss(inv, $"gpu[{g.Name}].architecture", g.ArchitectureNote ?? "Not reported.");
            if (g.Architecture == "Integrated" && g.DedicatedVramBytes is ulong v2 && v2 > 0) g.ArchitectureNote += $"; Windows reports {Fmt.Bytes(v2)} of firmware-reserved dedicated memory";
        }
    }

    // ---------------- Storage ----------------
    static readonly Dictionary<int, string> Bus = new() { [1] = "SCSI", [2] = "ATAPI", [3] = "ATA", [4] = "IEEE 1394", [6] = "Fibre Channel", [7] = "USB", [8] = "RAID controller", [9] = "iSCSI", [10] = "SAS", [11] = "SATA", [12] = "SD card", [13] = "MMC", [15] = "File-backed virtual", [16] = "Storage Spaces", [17] = "NVMe", [18] = "SCM", [19] = "UFS" };
    static void Disks(Inventory inv, Action<string?> partial)
    {
        List<Row> phys = new(), msDisks = new(), parts = new(), dd = new(), logical = new();
        bool haveStorage = true;
        try { phys = Wmi.Query(Storage, "SELECT * FROM MSFT_PhysicalDisk"); msDisks = Wmi.Query(Storage, "SELECT * FROM MSFT_Disk"); parts = Wmi.Query(Storage, "SELECT * FROM MSFT_Partition"); }
        catch (Exception e) { haveStorage = false; partial("Storage Management classes unavailable (" + e.Message + "); using Win32_DiskDrive, so media type and bus type may be missing."); }
        try { dd = Wmi.Query(Cimv2, "SELECT * FROM Win32_DiskDrive"); } catch (Exception e) { if (!haveStorage) throw; partial("Win32_DiskDrive unavailable: " + e.Message); }
        logical = Wmi.Query(Cimv2, "SELECT * FROM Win32_LogicalDisk");

        var byLetter = logical.Where(l => l.Str("DeviceID") != null).ToDictionary(l => l.Str("DeviceID")!.ToUpperInvariant(), l => l);
        VolumeInfo Vol(string letter) { byLetter.TryGetValue(letter, out var l); return new VolumeInfo { Letter = letter, Label = l?.Str("VolumeName"), FileSystem = l?.Str("FileSystem"), SizeBytes = l?.U64("Size") ?? 0, FreeBytes = l?.U64("FreeSpace") ?? 0 }; }

        var seen = new HashSet<int>();
        void AddVolumes(DiskInfo d)
        {
            foreach (var p in parts.Where(p => p.Int("DiskNumber") == d.Number))
            {
                int code = p.Int("DriveLetter") ?? 0; if (code < 'A' || code > 'Z') continue;
                string letter = ((char)code) + ":";
                if (d.Volumes.All(v => v.Letter != letter)) d.Volumes.Add(Vol(letter));
            }
            d.Volumes.Sort((a, b) => string.CompareOrdinal(a.Letter, b.Letter));
        }
        void Finish(DiskInfo d)
        {
            d.MediaPresent = d.SizeBytes > 0;
            if (!d.MediaPresent && (d.Connection is "USB" or "SD card" or "MMC" or "Unknown" || d.IsExternalOrRemovable)) { inv.HiddenEmptyReaders++; return; }
            if (d.Volumes.Count == 0 && haveStorage == false) { }
            inv.Disks.Add(d);
        }
        foreach (var p in phys)
        {
            int num = int.TryParse(p.Str("DeviceId"), out var n) ? n : -1; if (num < 0) continue; seen.Add(num);
            var drive = dd.FirstOrDefault(x => x.Int("Index") == num);
            var ms = msDisks.FirstOrDefault(x => x.Int("Number") == num);
            int bus = p.Int("BusType") ?? 0;
            string media = p.Int("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Not reported by Windows" };
            string conn = Bus.TryGetValue(bus, out var bn) ? bn : "Unknown";
            string? dmt = drive?.Str("MediaType");
            bool ext = bus == 7 || (dmt?.Contains("External", StringComparison.OrdinalIgnoreCase) ?? false) || (dmt?.Contains("Removable", StringComparison.OrdinalIgnoreCase) ?? false);
            var di = new DiskInfo
            {
                Number = num, Name = Clean(p.Str("FriendlyName") ?? p.Str("Model") ?? drive?.Str("Model") ?? "Disk " + num), Model = Clean(p.Str("Model") ?? drive?.Str("Model")), Manufacturer = Real(p.Str("Manufacturer") ?? drive?.Str("Manufacturer")),
                MediaType = media, Connection = conn, IsExternalOrRemovable = ext, SizeBytes = p.U64("Size") ?? drive?.U64("Size") ?? 0,
                PartitionStyle = ms?.Int("PartitionStyle") switch { 1 => "MBR", 2 => "GPT", 0 => "Uninitialized (RAW)", _ => null },
                Firmware = p.Str("FirmwareVersion"), SpindleRpm = p.U64("SpindleSpeed") is ulong sp && sp is > 0 and < 100000 ? (int)sp : null,
                WindowsHealthStatus = p.Int("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", 5 => "Unknown", _ => null },
                OperationalStatus = p.U16Array("OperationalStatus") is { Length: > 0 } os ? string.Join(", ", os.Select(OpStatus)) : null,
            };
            if (di.Model != null && di.Name.Equals(di.Model, StringComparison.OrdinalIgnoreCase)) di.Model = null;
            Id(inv, $"Disk {num} serial number", p.Str("SerialNumber") ?? drive?.Str("SerialNumber"));
            if (media == "Not reported by Windows" && di.SizeBytes > 0) Miss(inv, $"disk[{num}].mediaType", "Windows reports the media type (SSD/HDD) as unspecified; not inferred.");
            AddVolumes(di); Finish(di);
        }
        // Virtual or fallback entries
        foreach (var ms in msDisks.Where(x => x.Int("Number") is int n && !seen.Contains(n)))
        {
            int num = ms.Int("Number")!.Value; seen.Add(num);
            var di = new DiskInfo { Number = num, Name = Clean(ms.Str("FriendlyName") ?? "Disk " + num), MediaType = "Virtual or not a physical device", Connection = Bus.TryGetValue(ms.Int("BusType") ?? 0, out var b2) ? b2 : "Unknown", SizeBytes = ms.U64("Size") ?? 0, PartitionStyle = ms.Int("PartitionStyle") switch { 1 => "MBR", 2 => "GPT", _ => null } };
            AddVolumes(di); Finish(di);
        }
        foreach (var drive in dd.Where(x => x.Int("Index") is int n && !seen.Contains(n)))
        {
            int num = drive.Int("Index")!.Value; string iface = drive.Str("InterfaceType") ?? "Unknown"; string? dmt = drive.Str("MediaType");
            var di = new DiskInfo { Number = num, Name = Clean(drive.Str("Model") ?? "Disk " + num), Model = Clean(drive.Str("Model")), Manufacturer = Real(drive.Str("Manufacturer")), MediaType = "Not reported by Windows", Connection = iface == "IDE" ? "ATA/SATA (reported as IDE)" : iface, IsExternalOrRemovable = (dmt?.Contains("External") ?? false) || (dmt?.Contains("Removable") ?? false) || iface == "USB", SizeBytes = drive.U64("Size") ?? 0, WindowsHealthStatus = drive.Str("Status") };
            Id(inv, $"Disk {num} serial number", drive.Str("SerialNumber"));
            Miss(inv, $"disk[{num}].mediaType", "Storage Management data unavailable; SSD/HDD type not determined.");
            // Fallback volume mapping through the documented partition associations
            try
            {
                string did = drive.Str("DeviceID")!.Replace("\\", "\\\\");
                foreach (var part in Wmi.Query(Cimv2, $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{did}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
                    foreach (var l in Wmi.Query(Cimv2, $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{part.Str("DeviceID")}'}} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                        if (l.Str("DeviceID") is string letter) di.Volumes.Add(Vol(letter.ToUpperInvariant()));
            }
            catch { }
            Finish(di);
        }
        inv.Disks.Sort((a, b) => a.Number.CompareTo(b.Number));
        // Health detail beyond Windows' basic flag: storage reliability counters (no driver needed; some drives only report them to administrators)
        try
        {
            var rel = Wmi.Query(Storage, "SELECT DeviceId, Temperature, Wear, PowerOnHours, ReadErrorsTotal, WriteErrorsTotal FROM MSFT_StorageReliabilityCounter");
            foreach (var d in inv.Disks)
            {
                var r = rel.FirstOrDefault(x => x.Str("DeviceId") == d.Number.ToString());
                if (r == null) { d.ReliabilityNote = "Windows did not provide reliability counters for this drive."; continue; }
                d.TemperatureC = r.Int("Temperature") is int t and > 0 and < 150 ? t : null;
                d.WearPercentUsed = r.Int("Wear") is int w and >= 0 and <= 100 && d.MediaType == "SSD" ? w : null;
                d.PowerOnHours = r.U64("PowerOnHours") is ulong h and > 0 and < 1_000_000 ? (uint)h : null;
                d.ReadErrorsTotal = r.U64("ReadErrorsTotal") is ulong re and < uint.MaxValue ? (uint)re : null;
                d.WriteErrorsTotal = r.U64("WriteErrorsTotal") is ulong we and < uint.MaxValue ? (uint)we : null;
                if (d.TemperatureC == null && d.PowerOnHours == null && d.WearPercentUsed == null) d.ReliabilityNote = "This drive reports no temperature, wear or hours to a standard user (some report them only when run as administrator).";
            }
        }
        catch (Exception e) { foreach (var d in inv.Disks) d.ReliabilityNote = "Reliability counters unavailable: " + e.Message; }
    }

    static string OpStatus(ushort s) => s switch { 2 => "OK", 3 => "Degraded", 4 => "Stressed", 5 => "Predictive failure", 6 => "Error", 7 => "Non-recoverable error", 8 => "Starting", 9 => "Stopping", 10 => "Stopped", 11 => "In service", 12 => "No contact", 13 => "Lost communication", 0xD010 => "Transient error", 0xD011 => "Not ready", 0xD012 => "No media", 0xD013 => "Offline", 0xD014 => "Failed media", 0xD015 => "Hardware error", _ => "Code " + s };

    // ---------------- Network ----------------
    static void Nics(Inventory inv, Action<string?> partial)
    {
        try
        {
            foreach (var r in Wmi.Query(StdCim, "SELECT * FROM MSFT_NetAdapter"))
            {
                int medium = r.Int("NdisPhysicalMedium") ?? 0; string? pmt = r.Str("PhysicalMediaType");
                string kind = medium switch { 14 => "Ethernet", 9 => "Wi-Fi", 10 => "Bluetooth", 8 => "Mobile broadband", _ => pmt switch { "802.3" => "Ethernet", "Native 802.11" or "802.11" => "Wi-Fi", null or "Unspecified" => "Other", var o => o } };
                bool hw = r.Bool("HardwareInterface") ?? false, virt = r.Bool("Virtual") ?? false;
                string status = r.Str("Status") ?? "Unknown";
                var n = new NicInfo { Name = r.Str("Name") ?? "", Description = Clean(r.Str("InterfaceDescription") ?? r.Str("Name")), Kind = kind, Status = status, IsPhysical = hw && !virt, IsVirtual = virt };
                if (string.Equals(status, "Up", StringComparison.OrdinalIgnoreCase))
                {
                    n.ReceiveLinkBps = r.U64("ReceiveLinkSpeed") is > 0 and ulong rx ? rx : null; n.TransmitLinkBps = r.U64("TransmitLinkSpeed") is > 0 and ulong tx ? tx : null;
                }
                Id(inv, $"Network adapter {n.Name} MAC address", r.Str("PermanentAddress") ?? r.Str("MacAddress"));
                inv.Nics.Add(n);
            }
            return;
        }
        catch (Exception e) { partial("MSFT_NetAdapter unavailable (" + e.Message + "); using Win32_NetworkAdapter."); }
        foreach (var r in Wmi.Query(Cimv2, "SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE"))
        {
            int st = r.Int("NetConnectionStatus") ?? -1;
            var n = new NicInfo { Name = r.Str("NetConnectionID") ?? r.Str("Name") ?? "", Description = Clean(r.Str("Name")), Kind = "Other", Status = st switch { 2 => "Up", 0 => "Disconnected", 7 => "Disconnected", _ => "Unknown" }, IsPhysical = true };
            if (st == 2 && r.U64("Speed") is ulong sp && sp > 0 && sp < long.MaxValue) { n.ReceiveLinkBps = sp; n.TransmitLinkBps = sp; }
            Id(inv, $"Network adapter {n.Name} MAC address", r.Str("MACAddress"));
            inv.Nics.Add(n);
        }
    }

    // ---------------- Battery ----------------
    static void Battery(Inventory inv, Action<string?> partial)
    {
        var rows = Wmi.Query(Cimv2, "SELECT * FROM Win32_Battery");
        if (rows.Count == 0) { inv.BatteryNote = "No battery reported by Windows (normal for a desktop PC)."; return; }
        var r = rows[0];
        var b = new BatteryInfo
        {
            Name = r.Str("Name"), ChargePercent = r.Int("EstimatedChargeRemaining"),
            Status = r.Int("BatteryStatus") switch { 1 => "Discharging", 2 => "On AC power", 3 => "Fully charged", 4 => "Low", 5 => "Critical", 6 or 7 or 8 or 9 => "Charging", 11 => "Partially charged", _ => null },
            Chemistry = r.Int("Chemistry") switch { 3 => "Lead acid", 4 => "NiCd", 5 => "NiMH", 6 => "Lithium-ion", 7 => "Zinc air", 8 => "Lithium polymer", _ => null },
            EstimatedRuntimeMinutes = r.Int("EstimatedRunTime") is > 0 and < 71582788 and int m ? m : null,
        };
        try
        {
            var stat = Wmi.Query(RootWmi, "SELECT * FROM BatteryStaticData").FirstOrDefault(); var full = Wmi.Query(RootWmi, "SELECT * FROM BatteryFullChargedCapacity").FirstOrDefault(); var cyc = Wmi.Query(RootWmi, "SELECT * FROM BatteryCycleCount").FirstOrDefault();
            b.DesignCapacityMWh = stat?.Int("DesignedCapacity"); b.FullChargeCapacityMWh = full?.Int("FullChargedCapacity"); b.CycleCount = cyc?.Int("CycleCount") is > 0 and int cc ? cc : null;
            if (b.DesignCapacityMWh is > 0 && b.FullChargeCapacityMWh is > 0) b.WearPercent = Math.Max(0, 100.0 - b.FullChargeCapacityMWh.Value * 100.0 / b.DesignCapacityMWh.Value);
            Id(inv, "Battery serial number", stat?.Str("SerialNumber"));
        }
        catch (Exception e) { partial("Battery capacity classes unavailable: " + e.Message); Miss(inv, "battery.capacity", "root\\wmi battery classes not available: " + e.Message); }
        inv.Battery = b;
    }

    /// <summary>Quick refresh of free space for observations (no WMI).</summary>
    public static List<VolumeSnapshot> ReadVolumes()
    {
        var list = new List<VolumeSnapshot>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.DriveType == DriveType.Fixed && d.IsReady) list.Add(new VolumeSnapshot { Letter = d.Name.TrimEnd('\\'), Label = d.VolumeLabel, SizeBytes = (ulong)d.TotalSize, FreeBytes = (ulong)d.AvailableFreeSpace }); } catch { }
        }
        return list;
    }
}
