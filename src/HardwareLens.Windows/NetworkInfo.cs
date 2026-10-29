using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
namespace HardwareLens.Windows;

public sealed class WifiInfo
{
    public string? Ssid, Standard, Security; public int SignalPercent; public double? RxMbps, TxMbps; public bool NameWithheld;
}
public sealed class AdapterInfo
{
    public string Id = "", Name = "", Description = "", Kind = "", Status = "", Dns = "", Gateway = "", DhcpServer = "", DnsSuffix = "";
    public List<string> IPv4 = new(), IPv6 = new();
    public ulong? LinkBps; public int? Mtu; public bool Virtual, Up, DhcpEnabled;
    public WifiInfo? Wifi;
}
public sealed class ConnectionRow
{
    public int Pid; public string Proto = "", Local = "", Remote = "", State = "", Scope = "", Service = "";
    public int LocalPort, RemotePort; public bool Listening;
}
public sealed class PingResult { public string Target = "", Label = ""; public int Sent, Received; public double MinMs, AvgMs, MaxMs; public string? Error; }

/// <summary>Adapter details (managed .NET API + Wi-Fi API), per-program connections (IP Helper), and a user-triggered local ping. Nothing here contacts the internet on its own.</summary>
public static class NetworkInfo
{
    public static List<AdapterInfo> Adapters()
    {
        var wifi = WifiByGuid();
        var res = new List<AdapterInfo>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(n.Description, @"(Filter|Scheduler|Packet Driver)[^-]*-\d{4}$")) continue; // per-adapter filter bindings, not adapters
            var a = new AdapterInfo { Id = n.Id, Name = n.Name, Description = n.Description, Up = n.OperationalStatus == OperationalStatus.Up, Status = n.OperationalStatus.ToString() };
            a.Kind = n.NetworkInterfaceType switch { NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet", NetworkInterfaceType.Wireless80211 => "Wi-Fi", NetworkInterfaceType.Ppp => "VPN / dial-up", _ => n.NetworkInterfaceType.ToString() };
            if (n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) a.Kind = "Bluetooth";
            a.Virtual = System.Text.RegularExpressions.Regex.IsMatch(n.Description + " " + n.Name, "Hyper-V|Miniport|Direct Virtual|Kernel Debug|VMware|VirtualBox|Virtual|vEthernet|TAP-|WireGuard|Loopback|Npcap|WSL|Pseudo|Teredo|ISATAP|6to4|Wintun|Tailscale|ZeroTier|Fortinet|Cisco AnyConnect|OpenVPN|Docker", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            try
            {
                var p = n.GetIPProperties();
                foreach (var u in p.UnicastAddresses) { if (u.Address.AddressFamily == AddressFamily.InterNetwork) a.IPv4.Add($"{u.Address}/{PrefixLen(u.IPv4Mask)}"); else if (u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal) a.IPv6.Add(u.Address.ToString()); }
                a.Gateway = string.Join(", ", p.GatewayAddresses.Select(g => g.Address).Where(g => g.AddressFamily == AddressFamily.InterNetwork || a.IPv4.Count == 0).Select(g => g.ToString()));
                a.Dns = string.Join(", ", p.DnsAddresses.Where(d => !d.IsIPv6LinkLocal && !d.IsIPv6SiteLocal).Select(d => d.ToString()));
                a.DnsSuffix = p.DnsSuffix; a.DhcpServer = string.Join(", ", p.DhcpServerAddresses.Select(d => d.ToString()));
                try { var v4 = p.GetIPv4Properties(); a.DhcpEnabled = v4.IsDhcpEnabled; a.Mtu = v4.Mtu; } catch { }
            }
            catch { }
            if (a.Up) { try { a.LinkBps = n.Speed > 0 ? (ulong)n.Speed : null; } catch { } }
            if (wifi.TryGetValue(n.Id.Trim('{', '}').ToLowerInvariant(), out var w)) a.Wifi = w;
            res.Add(a);
        }
        return res.OrderByDescending(a => a.Up && a.IPv4.Count > 0).ThenBy(a => a.Virtual).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static int PrefixLen(IPAddress? mask) { if (mask == null) return 0; int c = 0; foreach (var b in mask.GetAddressBytes()) for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) c++; return c; }

    // ---------- Wi-Fi (wlanapi) ----------
    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiated, out nint handle);
    [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(nint handle, ref Guid iface, int opcode, nint reserved, out uint size, out nint data, out int type);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(nint p);

    static Dictionary<string, WifiInfo> WifiByGuid()
    {
        var res = new Dictionary<string, WifiInfo>();
        nint h = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out h) != 0) return res;
            if (WlanEnumInterfaces(h, 0, out nint list) != 0) return res;
            try
            {
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; i++)
                {
                    nint item = list + 8 + i * 532; var guid = Marshal.PtrToStructure<Guid>(item); int state = Marshal.ReadInt32(item + 16 + 512);
                    if (state != 1) continue; // wlan_interface_state_connected
                    if (WlanQueryInterface(h, ref guid, 7, 0, out uint size, out nint data, out _) != 0 || size < 604) continue;
                    try
                    {
                        int ssidLen = Marshal.ReadInt32(data + 520); string? ssid = ssidLen is > 0 and <= 32 ? System.Text.Encoding.UTF8.GetString(Enumerable.Range(0, ssidLen).Select(k => Marshal.ReadByte(data + 524 + k)).ToArray()) : null;
                        int phy = Marshal.ReadInt32(data + 568), sig = (int)(uint)Marshal.ReadInt32(data + 576), rx = Marshal.ReadInt32(data + 580), tx = Marshal.ReadInt32(data + 584);
                        int sec = Marshal.ReadInt32(data + 588), auth = Marshal.ReadInt32(data + 596);
                        res[guid.ToString().ToLowerInvariant()] = new WifiInfo
                        {
                            Ssid = ssid, NameWithheld = ssid == null, SignalPercent = Math.Clamp(sig, 0, 100), RxMbps = rx > 0 ? rx / 1000.0 : null, TxMbps = tx > 0 ? tx / 1000.0 : null,
                            Standard = phy switch { 4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "Wi-Fi 4 (802.11n)", 8 => "Wi-Fi 5 (802.11ac)", 9 => "802.11ad", 10 => "Wi-Fi 6 (802.11ax)", 11 => "Wi-Fi 7 (802.11be)", _ => null },
                            Security = sec == 0 ? "Open (no encryption)" : auth switch { 1 => "Open", 3 => "WPA-Enterprise", 4 => "WPA-Personal", 6 => "WPA2-Enterprise", 7 => "WPA2-Personal", 8 => "WPA3-Enterprise", 9 => "WPA3-Personal", 10 => "OWE", _ => "Encrypted (type " + auth + ")" }
                        };
                    }
                    finally { WlanFreeMemory(data); }
                }
            }
            finally { WlanFreeMemory(list); }
        }
        catch { }
        finally { if (h != 0) WlanCloseHandle(h, 0); }
        return res;
    }

    // ---------- connections per program (IP Helper) ----------
    [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedTcpTable(nint table, ref int size, bool sort, int af, int cls, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedUdpTable(nint table, ref int size, bool sort, int af, int cls, uint reserved);

    static readonly string[] TcpStates = { "", "Closed", "Listening", "Connecting", "Connecting", "Connected", "Closing", "Closing", "Closing", "Closing", "Closing", "Closing (waiting)", "Closed" };

    static byte[] Table(bool tcp, int af)
    {
        int size = 0; nint buf = 0;
        try
        {
            for (int i = 0; i < 5; i++)
            {
                uint rc = tcp ? GetExtendedTcpTable(buf, ref size, false, af, 5, 0) : GetExtendedUdpTable(buf, ref size, false, af, 1, 0);
                if (rc == 0) { var arr = new byte[size]; Marshal.Copy(buf, arr, 0, size); return arr; }
                if (rc != 122) return Array.Empty<byte>();
                if (buf != 0) Marshal.FreeHGlobal(buf); size += 4096; buf = Marshal.AllocHGlobal(size);
            }
            return Array.Empty<byte>();
        }
        finally { if (buf != 0) Marshal.FreeHGlobal(buf); }
    }

    static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    public static List<ConnectionRow> Connections()
    {
        var res = new List<ConnectionRow>();
        void Tcp(int af)
        {
            var t = Table(true, af); if (t.Length < 4) return; int n = BitConverter.ToInt32(t, 0); int rowSize = af == 2 ? 24 : 56;
            for (int i = 0; i < n && 4 + (i + 1) * rowSize <= t.Length; i++)
            {
                int o = 4 + i * rowSize; ConnectionRow r;
                if (af == 2)
                {
                    uint st = BitConverter.ToUInt32(t, o); var la = new IPAddress(BitConverter.ToUInt32(t, o + 4)); var ra = new IPAddress(BitConverter.ToUInt32(t, o + 12));
                    r = Row("TCP", la, Port(BitConverter.ToUInt32(t, o + 8)), ra, Port(BitConverter.ToUInt32(t, o + 16)), (int)st, BitConverter.ToInt32(t, o + 20));
                }
                else
                {
                    var la = new IPAddress(t.AsSpan(o, 16)); var ra = new IPAddress(t.AsSpan(o + 24, 16)); uint st = BitConverter.ToUInt32(t, o + 48);
                    r = Row("TCP", la, Port(BitConverter.ToUInt32(t, o + 20)), ra, Port(BitConverter.ToUInt32(t, o + 44)), (int)st, BitConverter.ToInt32(t, o + 52));
                }
                res.Add(r);
            }
        }
        void Udp(int af)
        {
            var t = Table(false, af); if (t.Length < 4) return; int n = BitConverter.ToInt32(t, 0); int rowSize = af == 2 ? 12 : 28;
            for (int i = 0; i < n && 4 + (i + 1) * rowSize <= t.Length; i++)
            {
                int o = 4 + i * rowSize;
                var la = af == 2 ? new IPAddress(BitConverter.ToUInt32(t, o)) : new IPAddress(t.AsSpan(o, 16));
                int port = Port(BitConverter.ToUInt32(t, af == 2 ? o + 4 : o + 20)); int pid = BitConverter.ToInt32(t, af == 2 ? o + 8 : o + 24);
                res.Add(new ConnectionRow { Pid = pid, Proto = "UDP", Local = la.ToString(), LocalPort = port, Remote = "", State = "Listening for traffic", Listening = true, Scope = IsLoopback(la) ? "This computer only" : "Any network", Service = ServiceName(port) });
            }
        }
        try { Tcp(2); Tcp(23); Udp(2); Udp(23); } catch { }
        return res;
    }

    static ConnectionRow Row(string proto, IPAddress la, int lp, IPAddress ra, int rp, int state, int pid)
    {
        bool listen = state == 2;
        var r = new ConnectionRow { Pid = pid, Proto = proto, Local = la.ToString(), LocalPort = lp, Remote = listen ? "" : ra.ToString(), RemotePort = listen ? 0 : rp, State = state is >= 0 and < 13 ? TcpStates[state] : state.ToString(), Listening = listen };
        if (listen) { r.Scope = IsLoopback(la) ? "This computer only" : "Any network"; r.Service = ServiceName(lp); }
        else { r.Scope = IsLoopback(ra) ? "This computer" : IsPrivate(ra) ? "Local network" : "Internet"; r.Service = ServiceName(rp); }
        return r;
    }

    static bool IsLoopback(IPAddress a) => IPAddress.IsLoopback(a);
    public static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetworkV6) { var b = a.GetAddressBytes(); return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6Loopback); }
        var x = a.GetAddressBytes(); return x[0] == 10 || x[0] == 172 && x[1] is >= 16 and <= 31 || x[0] == 192 && x[1] == 168 || x[0] == 169 && x[1] == 254 || x[0] == 100 && x[1] is >= 64 and <= 127 || x[0] == 0;
    }

    static string ServiceName(int port) => port switch
    {
        20 or 21 => "FTP", 22 => "SSH", 23 => "Telnet", 25 or 465 or 587 => "Email (SMTP)", 53 => "DNS", 67 or 68 => "DHCP", 80 or 8080 => "Web (HTTP)", 110 or 995 => "Email (POP3)", 123 => "Time sync", 135 => "Windows RPC", 137 or 138 or 139 => "Windows file sharing (NetBIOS)",
        143 or 993 => "Email (IMAP)", 389 or 636 => "Directory (LDAP)", 443 or 8443 => "Secure web (HTTPS)", 445 => "Windows file sharing (SMB)", 1900 => "Device discovery (UPnP)", 3389 => "Remote Desktop", 5353 => "Local name lookup (mDNS)", 5355 => "Local name lookup (LLMNR)", 5985 or 5986 => "Remote management (WinRM)",
        3306 => "MySQL", 5432 => "PostgreSQL", 1433 => "SQL Server", 6379 => "Redis", 5900 => "VNC", 9100 => "Printer", 631 => "Printer (IPP)", _ => ""
    };

    // ---------- ping (only when the user asks) ----------
    public static async Task<PingResult> PingAsync(string label, string address, int count = 4)
    {
        var res = new PingResult { Target = address, Label = label };
        try
        {
            using var p = new Ping(); var times = new List<long>();
            for (int i = 0; i < count; i++)
            {
                res.Sent++;
                var r = await p.SendPingAsync(address, 1500);
                if (r.Status == IPStatus.Success) { res.Received++; times.Add(r.RoundtripTime); }
                await Task.Delay(150);
            }
            if (times.Count > 0) { res.MinMs = times.Min(); res.MaxMs = times.Max(); res.AvgMs = times.Average(); }
        }
        catch (Exception e) { res.Error = e.InnerException?.Message ?? e.Message; }
        return res;
    }
}
