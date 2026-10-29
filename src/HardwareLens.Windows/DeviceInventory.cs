using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace HardwareLens.Windows;

public sealed class DeviceItem
{
    public string Name = "", Category = "", Connection = "", Status = "OK";
    public string? Manufacturer, VidPid, Location, DriverProvider, DriverVersion, Model, Serial, Note, ClassName;
    public DateTime? DriverDate; public int? Battery; public bool NameIsGeneric, HasProblem, Internal;
    public List<string> InstanceIds = new(), HardwareIds = new();
    public int Nodes;
}
public sealed class PrinterItem { public string Name = "", Driver = "", Port = "", Status = "", Kind = ""; public bool IsDefault, Virtual, Offline; }
public sealed class MonitorItem { public string Name = "", Maker = "", Connection = "", Mode = "", Size = ""; public string? Year, Serial; }
public sealed class AudioItem { public string Name = "", Direction = "", State = ""; }
public sealed class DeviceSnapshot
{
    public DateTimeOffset At = DateTimeOffset.Now; public double Seconds;
    public List<DeviceItem> Peripherals = new(), Internal = new();
    public List<PrinterItem> Printers = new(); public List<MonitorItem> Monitors = new(); public List<AudioItem> Audio = new();
    public List<string> Problems = new();
}

/// <summary>
/// Lists connected hardware with the names the devices themselves report (USB/Bluetooth product strings), grouping the many Windows
/// device nodes of one physical device into one entry by its container ID. Generic driver names ("HID-compliant mouse") are only used when nothing better exists.
/// </summary>
public static unsafe class DeviceInventory
{
    // ---- SetupAPI / CfgMgr ----
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern nint SetupDiGetClassDevsW(nint classGuid, string? enumerator, nint hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiGetDevicePropertyW(nint set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[]? buf, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    static readonly Guid GDev = new("a45c254e-df1c-4efd-8020-67d146a850e0"), GBus = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), GCont = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
        GDrv = new("a8b865dd-2e3d-4094-ad97-e593a70c75d5"), GInst = new("78c34fc8-104a-4aca-9ea4-524d52996e57"), GBt = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
    static readonly Guid Builtin = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

    sealed class Node
    {
        public string Id = "", Class = "", Enumerator = ""; public string? Friendly, Desc, Bus, Mfr, Location, DrvProvider, DrvVersion; public Guid Container; public DateTime? DrvDate;
        public List<string> HwIds = new(); public uint Problem; public bool HasProblem; public int? Battery;
        public IEnumerable<string> Names() { foreach (var n in new[] { Bus, Friendly, Desc }) if (!string.IsNullOrWhiteSpace(n)) yield return n!.Trim(); }
    }

    static object? Prop(nint set, ref SP_DEVINFO_DATA d, Guid fmt, uint pid)
    {
        var key = new DEVPROPKEY { fmtid = fmt, pid = pid };
        SetupDiGetDevicePropertyW(set, ref d, ref key, out uint type, null, 0, out uint need, 0);
        if (need == 0 || need > 65536) return null;
        var buf = new byte[need];
        if (!SetupDiGetDevicePropertyW(set, ref d, ref key, out type, buf, need, out need, 0)) return null;
        switch (type)
        {
            case 0x12: return Encoding.Unicode.GetString(buf).TrimEnd('\0');
            case 0x2012: return Encoding.Unicode.GetString(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            case 0xD: return buf.Length >= 16 ? new Guid(buf.AsSpan(0, 16)) : null;
            case 0x10: try { return DateTime.FromFileTimeUtc(BitConverter.ToInt64(buf, 0)); } catch { return null; }
            case 7: return BitConverter.ToUInt32(buf, 0);
            case 3: return buf[0];
            default: return null;
        }
    }
    static string? S(object? o) => o as string is { Length: > 0 } s ? s : null;

    static readonly string[] GenericBits =
    { "hid-compliant", "hid keyboard", "usb input device", "usb composite", "usb mass storage", "usb device", "generic", "standard ", "unknown", "usb audio device", "usb receiver", "bluetooth device", "usb serial", "vendor-defined", "consumer control", "system controller", "wireless receiver", "usb root", "(standard" };
    static bool Generic(string n) { var l = n.ToLowerInvariant(); return GenericBits.Any(l.Contains); }

    static string Pretty(string cls) => cls switch
    {
        "Keyboard" => "Keyboard", "Mouse" => "Mouse", "Camera" or "Image" => "Camera / scanner", "MEDIA" or "AudioEndpoint" => "Audio", "Bluetooth" => "Bluetooth device", "DiskDrive" or "WPD" or "CDROM" or "USBSTOR" => "Storage / phone",
        "Biometric" => "Fingerprint / biometric", "SmartCardReader" => "Smart card reader", "Net" => "Network adapter", "Ports" => "Serial / COM port", "HIDClass" => "Input device (controller, headset, etc.)", "Printer" or "PrintQueue" => "Printer",
        "USB" => "USB device", "Display" => "Graphics", "Monitor" => "Monitor", "Battery" => "Battery", "Sensor" => "Sensor", "Processor" => "Processor", "SCSIAdapter" or "HDC" => "Storage controller", "Modem" => "Modem", var c => c
    };

    public static DeviceSnapshot Collect()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); var snap = new DeviceSnapshot();
        try { Nodes(snap); } catch (Exception e) { snap.Problems.Add("Device list: " + e.Message); }
        try { snap.Printers = Printers(); } catch (Exception e) { snap.Problems.Add("Printers: " + e.Message); }
        try { snap.Monitors = Monitors(); } catch (Exception e) { snap.Problems.Add("Monitors: " + e.Message); }
        try { snap.Audio = Audio(); } catch (Exception e) { snap.Problems.Add("Audio: " + e.Message); }
        snap.Seconds = sw.Elapsed.TotalSeconds; return snap;
    }

    static void Nodes(DeviceSnapshot snap)
    {
        nint set = SetupDiGetClassDevsW(0, null, 0, 0x2 | 0x4); // DIGCF_PRESENT | DIGCF_ALLCLASSES
        if (set == -1 || set == 0) throw new InvalidOperationException("SetupDiGetClassDevs failed (" + Marshal.GetLastWin32Error() + ")");
        var nodes = new List<Node>();
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
            {
                var n = new Node
                {
                    Id = S(Prop(set, ref d, GInst, 256)) ?? "", Friendly = S(Prop(set, ref d, GDev, 14)), Desc = S(Prop(set, ref d, GDev, 2)), Bus = S(Prop(set, ref d, GBus, 4)), Mfr = S(Prop(set, ref d, GDev, 13)),
                    Class = S(Prop(set, ref d, GDev, 9)) ?? "", Enumerator = S(Prop(set, ref d, GDev, 24)) ?? "", Location = S(Prop(set, ref d, GDev, 15)),
                    DrvProvider = S(Prop(set, ref d, GDrv, 9)), DrvVersion = S(Prop(set, ref d, GDrv, 3)), DrvDate = Prop(set, ref d, GDrv, 2) as DateTime?,
                    Container = Prop(set, ref d, GCont, 2) is Guid g ? g : Guid.Empty,
                };
                if (Prop(set, ref d, GDev, 3) is string[] hw) n.HwIds = hw.ToList();
                if (Prop(set, ref d, GBt, 2) is byte b && b <= 100) n.Battery = b;
                if (CM_Get_DevNode_Status(out uint st, out uint prob, d.DevInst, 0) == 0 && (st & 0x400) != 0) { n.HasProblem = true; n.Problem = prob; }
                nodes.Add(n);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        var skipClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Printer", "PrintQueue", "Monitor", "Volume", "VolumeSnapshot", "Processor", "SoftwareDevice", "SoftwareComponent", "Computer", "System", "Firmware", "Extension", "HIDClass_" };
        foreach (var grp in nodes.Where(n => n.Container != Guid.Empty && n.Container != Builtin).GroupBy(n => n.Container))
        {
            var list = grp.ToList(); var classes = list.Select(n => n.Class).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (classes.Contains("Printer") || classes.Contains("PrintQueue") || classes.Contains("Monitor")) continue;
            if (list.All(n => n.Enumerator.Equals("SWD", StringComparison.OrdinalIgnoreCase) && !classes.Contains("Bluetooth"))) continue;
            snap.Peripherals.Add(Build(list, classes, false));
        }
        // built-in devices: one entry per useful device node
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Display", "Net", "MEDIA", "DiskDrive", "Keyboard", "Mouse", "Bluetooth", "Camera", "Image", "Biometric", "Battery", "SCSIAdapter", "HDC", "Sensor", "SmartCardReader", "Modem", "USB", "HIDClass", "System" };
        foreach (var n in nodes.Where(n => (n.Container == Guid.Empty || n.Container == Builtin) && keep.Contains(n.Class) && !n.Enumerator.Equals("SWD", StringComparison.OrdinalIgnoreCase)))
        {
            string name = n.Names().FirstOrDefault(x => !Generic(x)) ?? n.Names().FirstOrDefault() ?? n.Id;
            if (n.Class is "System" or "HIDClass" or "USB" && (Generic(name) || Regex.IsMatch(name, @"^(HID|ACPI|PCI|Microsoft|Composite|Root|Generic)|Hub|Enumerator|Interface|Bridge", RegexOptions.IgnoreCase))) continue;
            var it = Build(new List<Node> { n }, new HashSet<string>(new[] { n.Class }, StringComparer.OrdinalIgnoreCase), true); snap.Internal.Add(it);
        }
        snap.Peripherals = snap.Peripherals.OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        snap.Internal = snap.Internal.OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static DeviceItem Build(List<Node> list, HashSet<string> classes, bool internalDev)
    {
        var it = new DeviceItem { Internal = internalDev, Nodes = list.Count };
        // name: best non-generic candidate, preferring the string the device itself reports over a driver-supplied one
        string? best = null; int bestScore = -1;
        foreach (var n in list) foreach (var name in n.Names())
        {
            int score = (Generic(name) ? 0 : 10) + (name == n.Bus ? 3 : 0) + (n.Class.Equals("USB", StringComparison.OrdinalIgnoreCase) ? 2 : 0) + (n.Class.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase) ? 1 : 0) + Math.Min(name.Length, 40) / 40;
            if (score > bestScore) { bestScore = score; best = name; }
        }
        it.Name = best ?? list[0].Id; it.NameIsGeneric = Generic(it.Name);
        var ids = list.SelectMany(n => n.HwIds).ToList(); it.HardwareIds = ids.Distinct().Take(12).ToList(); it.InstanceIds = list.Select(n => n.Id).ToList();
        // vendor / product id
        string? vid = null, pid = null;
        foreach (var h in ids) { var m = Regex.Match(h, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase); if (m.Success) { vid = m.Groups[1].Value.ToUpperInvariant(); pid = m.Groups[2].Value.ToUpperInvariant(); break; } }
        if (vid != null) { it.VidPid = $"{vid}:{pid}"; it.Manufacturer = UsbVendors.Name(vid) ?? "USB vendor ID " + vid; }
        if (it.Manufacturer == null || it.Manufacturer.StartsWith("USB vendor ID"))
        {
            var mf = list.Select(n => n.Mfr).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !x!.StartsWith("(") && !x.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("Generic", StringComparison.OrdinalIgnoreCase) && !x.Contains("WinUsb", StringComparison.OrdinalIgnoreCase));
            if (mf != null) it.Manufacturer = mf;
            else if (vid == null) it.Manufacturer = list.Select(n => n.Mfr).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !x!.StartsWith("("));
        }
        // category
        string cls = classes.Contains("Keyboard") && classes.Contains("Mouse") ? "Keyboard and mouse" :
            new[] { "Keyboard", "Mouse", "Camera", "Image", "MEDIA", "AudioEndpoint", "Biometric", "SmartCardReader", "Bluetooth", "DiskDrive", "WPD", "Net", "Ports", "Display", "HIDClass", "USB" }.Where(classes.Contains).Select(Pretty).FirstOrDefault() ?? Pretty(classes.FirstOrDefault() ?? "Other");
        if (cls == "USB device" && Regex.IsMatch(it.Name, "hub", RegexOptions.IgnoreCase)) cls = "USB hub";
        it.Category = cls; it.ClassName = string.Join(", ", classes.Where(c => c.Length > 0));
        // connection
        var enums = list.Select(n => n.Enumerator.ToUpperInvariant()).ToList();
        it.Connection = enums.Any(e => e == "USB") ? "USB" : enums.Any(e => e.StartsWith("BTH")) ? "Bluetooth" : internalDev ? (list[0].Enumerator.ToUpperInvariant() is "PCI" ? "PCI Express / built-in" : "Built into this computer") : enums.Any(e => e == "ACPI") ? "PS/2 or built-in" : "Other connection";
        it.Location = list.Select(n => n.Location).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var drv = list.FirstOrDefault(n => n.DrvProvider != null && !n.DrvProvider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault(n => n.DrvProvider != null);
        if (drv != null) { it.DriverProvider = drv.DrvProvider; it.DriverVersion = drv.DrvVersion; it.DriverDate = drv.DrvDate; }
        it.Battery = list.Select(n => n.Battery).FirstOrDefault(b => b != null);
        var prob = list.FirstOrDefault(n => n.HasProblem);
        if (prob != null) { it.HasProblem = true; it.Status = "Needs attention: " + ProblemText(prob.Problem); }
        // serial: only when the device itself exposes one (Windows invents instance suffixes containing '&' for devices without)
        var usbNode = list.FirstOrDefault(n => n.Id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase));
        if (usbNode != null) { var tail = usbNode.Id[(usbNode.Id.LastIndexOf('\\') + 1)..]; if (tail.Length > 0 && !tail.Contains('&')) it.Serial = tail; }
        if (it.NameIsGeneric) it.Note = "This device does not report a specific product name to Windows, so only the generic driver name is available.";
        return it;
    }

    static string ProblemText(uint code) => code switch
    {
        1 => "not configured correctly (code 1)", 3 => "driver may be corrupted (code 3)", 10 => "cannot start (code 10)", 12 => "cannot find free resources (code 12)", 14 => "needs a restart (code 14)", 18 => "drivers need reinstalling (code 18)",
        19 => "registry problem (code 19)", 22 => "disabled in Device Manager (code 22)", 24 => "not present or not working (code 24)", 28 => "no driver installed (code 28)", 31 => "not working properly (code 31)", 37 => "driver failed to initialize (code 37)",
        39 => "driver missing or corrupt (code 39)", 43 => "stopped it after reporting a problem (code 43)", 45 => "not currently connected (code 45)", 48 => "driver blocked (code 48)", 52 => "driver signature cannot be verified (code 52)", _ => "problem code " + code
    };

    // ---------- printers ----------
    static List<PrinterItem> Printers()
    {
        var res = new List<PrinterItem>();
        foreach (var r in Wmi.Query(@"root\cimv2", "SELECT Name, DriverName, PortName, Default, WorkOffline, PrinterStatus, Network, Local FROM Win32_Printer", 10))
        {
            string name = r.Str("Name") ?? "", port = r.Str("PortName") ?? "", drv = r.Str("DriverName") ?? "";
            bool virt = Regex.IsMatch(port, @"^(PORTPROMPT|nul|FILE|XPSPort|SHRFAX|Microsoft)", RegexOptions.IgnoreCase) || Regex.IsMatch(drv + name, @"Print To PDF|XPS Document Writer|OneNote|Fax|Send To", RegexOptions.IgnoreCase);
            int st = r.Int("PrinterStatus") ?? 2; bool offline = r.Bool("WorkOffline") == true || st == 7;
            res.Add(new PrinterItem
            {
                Name = name, Driver = drv, Port = port, IsDefault = r.Bool("Default") == true, Virtual = virt, Offline = offline,
                Kind = virt ? "Virtual (saves to a file)" : r.Bool("Network") == true || Regex.IsMatch(port, @"^(IP_|WSD|\d+\.\d+)", RegexOptions.IgnoreCase) ? "Network printer" : "Local printer",
                Status = offline ? "Offline" : st switch { 3 => "Ready", 4 => "Printing", 5 => "Warming up", 6 => "Stopped printing", _ => "Status not reported" }
            });
        }
        return res.OrderBy(p => p.Virtual).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- monitors ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE { public uint cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString; public uint StateFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public uint dmFields; public int dmPositionX, dmPositionY; public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName; public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevicesW(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);

    static string Decode(ushort[]? a) => a == null ? "" : new string(a.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();

    static readonly Dictionary<string, string> PnpMakers = new(StringComparer.OrdinalIgnoreCase)
    { ["GSM"] = "LG", ["DEL"] = "Dell", ["SAM"] = "Samsung", ["AUS"] = "ASUS", ["ACR"] = "Acer", ["BNQ"] = "BenQ", ["GBT"] = "Gigabyte", ["MSI"] = "MSI", ["AOC"] = "AOC", ["HWP"] = "HP", ["HPN"] = "HP", ["LEN"] = "Lenovo", ["PHL"] = "Philips", ["VSC"] = "ViewSonic", ["SNY"] = "Sony", ["IVM"] = "Iiyama", ["NEC"] = "NEC", ["APP"] = "Apple", ["EIZ"] = "EIZO", ["MEI"] = "Panasonic", ["TSB"] = "Toshiba", ["CMN"] = "Chimei Innolux", ["BOE"] = "BOE", ["LGD"] = "LG Display", ["SDC"] = "Samsung Display", ["AUO"] = "AU Optronics" };

    static List<MonitorItem> Monitors()
    {
        // current mode per attached monitor, keyed by the 7-character PnP code (e.g. GSM5D5E)
        var modes = new List<(string code, string mode)>();
        var adapter = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref adapter, 0); i++)
        {
            if ((adapter.StateFlags & 1) == 0) { adapter = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() }; continue; }
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            string mode = EnumDisplaySettingsW(adapter.DeviceName, -1, ref dm) ? $"{dm.dmPelsWidth} × {dm.dmPelsHeight} at {dm.dmDisplayFrequency} Hz" : "";
            var mon = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevicesW(adapter.DeviceName, 0, ref mon, 0)) { var m = Regex.Match(mon.DeviceID ?? "", @"MONITOR\\([A-Z0-9]{7})", RegexOptions.IgnoreCase); modes.Add((m.Success ? m.Groups[1].Value.ToUpperInvariant() : "", mode)); }
            adapter = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
        var res = new List<MonitorItem>(); var sizes = new Dictionary<string, string>();
        try { foreach (var r in Wmi.Query(@"root\wmi", "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", 8)) { double w = r.Int("MaxHorizontalImageSize") ?? 0, h = r.Int("MaxVerticalImageSize") ?? 0; if (w > 0 && h > 0) sizes[r.Str("InstanceName") ?? ""] = $"{Math.Sqrt(w * w + h * h) / 2.54:0.#}-inch diagonal"; } } catch { }
        foreach (var r in Wmi.Query(@"root\wmi", "SELECT InstanceName, ManufacturerName, UserFriendlyName, ProductCodeID, YearOfManufacture, SerialNumberID FROM WmiMonitorID", 8))
        {
            string inst = r.Str("InstanceName") ?? ""; string maker = Decode(r.U16Array("ManufacturerName")); string model = Decode(r.U16Array("UserFriendlyName")); string prod = Decode(r.U16Array("ProductCodeID"));
            string code = (maker + prod).ToUpperInvariant();
            var mi = modes.FindIndex(m => m.code == code); string mode = ""; if (mi >= 0) { mode = modes[mi].mode; modes.RemoveAt(mi); }
            string makerName = PnpMakers.TryGetValue(maker, out var mn) ? mn : maker;
            string baseInst = Regex.Replace(inst, @"_\d+$", "");
            res.Add(new MonitorItem
            {
                Name = model.Length > 0 ? model : (makerName.Length > 0 ? makerName + " monitor" : "Monitor"), Maker = makerName, Mode = mode, Size = sizes.GetValueOrDefault(inst) ?? sizes.GetValueOrDefault(baseInst) ?? "",
                Year = r.Int("YearOfManufacture") is int y and > 1990 ? y.ToString() : null, Serial = Decode(r.U16Array("SerialNumberID")) is { Length: > 0 } sn ? sn : null, Connection = ""
            });
        }
        return res;
    }

    // ---------- audio endpoints ----------
    static List<AudioItem> Audio()
    {
        var res = new List<AudioItem>();
        foreach (var (dir, sub) in new[] { ("Output", "Render"), ("Input", "Capture") })
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\" + sub); if (root == null) continue;
            foreach (var id in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(id); if (k == null) continue;
                int state = Convert.ToInt32(k.GetValue("DeviceState") ?? 0); if (state != 1 && state != 8) continue; // active or unplugged
                using var p = k.OpenSubKey("Properties");
                string? name = p?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},14") as string ?? p?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                res.Add(new AudioItem { Name = name!, Direction = dir, State = state == 1 ? "Active" : "Unplugged" });
            }
        }
        return res.OrderBy(a => a.Direction).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
