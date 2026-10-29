using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
namespace HardwareLens.Windows;

public sealed class StartupItem
{
    public string Id = "", Name = "", Command = "", Location = "", Scope = "";
    public string? ExePath, Publisher, Product;
    public bool Enabled, NeedsAdmin, FolderItem;
    // internal addressing
    internal RegistryHive Hive; internal string RunKey = "", ApprovedKey = "", ValueName = "";
}

/// <summary>
/// Lists programs that start with Windows (Run registry keys and Startup folders) and turns them on or off the same way
/// Windows' own Startup Apps page does: by writing the StartupApproved flag. Nothing is deleted, so a change is fully reversible.
/// </summary>
public static class StartupManager
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string Appr = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    static bool IsEnabled(byte[]? v) => v == null || v.Length == 0 || (v[0] & 1) == 0;

    static byte[]? ReadApproved(RegistryHive hive, string sub, string name)
    {
        try { using var bk = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64); using var k = bk.OpenSubKey(Appr + sub); return k?.GetValue(name) as byte[]; } catch { return null; }
    }

    public static List<StartupItem> List(out List<string> problems)
    {
        var probs = new List<string>(); var res = new List<StartupItem>();
        void Reg(RegistryHive hive, RegistryView view, string key, string approvedSub, string scope, string location, bool admin)
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view); using var k = bk.OpenSubKey(key); if (k == null) return;
                foreach (var n in k.GetValueNames())
                {
                    if (n.Length == 0) continue; string cmd = Convert.ToString(k.GetValue(n)) ?? "";
                    var it = new StartupItem { Id = $"{hive}|{key}|{n}", Name = n, Command = cmd, Location = location, Scope = scope, NeedsAdmin = admin, Hive = hive, RunKey = key, ApprovedKey = approvedSub, ValueName = n, Enabled = IsEnabled(ReadApproved(hive, approvedSub, n)) };
                    Fill(it, cmd); res.Add(it);
                }
            }
            catch (Exception e) { probs.Add(location + ": " + e.Message); }
        }
        Reg(RegistryHive.CurrentUser, RegistryView.Registry64, Run, "Run", "This user", "Registry (current user)", false);
        Reg(RegistryHive.LocalMachine, RegistryView.Registry64, Run, "Run", "All users", "Registry (all users)", true);
        Reg(RegistryHive.LocalMachine, RegistryView.Registry64, Run32, "Run32", "All users", "Registry (all users, 32-bit)", true);
        void Folder(Environment.SpecialFolder sf, RegistryHive hive, string scope, string location, bool admin)
        {
            try
            {
                string dir = Environment.GetFolderPath(sf); if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    string fn = Path.GetFileName(f); if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var it = new StartupItem { Id = $"{hive}|folder|{fn}", Name = Path.GetFileNameWithoutExtension(fn), Command = f, Location = location, Scope = scope, NeedsAdmin = admin, FolderItem = true, Hive = hive, ApprovedKey = "StartupFolder", ValueName = fn, Enabled = IsEnabled(ReadApproved(hive, "StartupFolder", fn)) };
                    Fill(it, f); res.Add(it);
                }
            }
            catch (Exception e) { probs.Add(location + ": " + e.Message); }
        }
        Folder(Environment.SpecialFolder.Startup, RegistryHive.CurrentUser, "This user", "Startup folder (current user)", false);
        Folder(Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine, "All users", "Startup folder (all users)", true);
        problems = probs; return res.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase) { "rundll32", "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "regsvr32", "msiexec", "explorer", "conhost", "cscript" };
    static void Fill(StartupItem it, string cmd)
    {
        string? exe = ExtractExe(cmd);
        if (exe != null && File.Exists(exe))
        {
            it.ExePath = exe;
            try { var v = FileVersionInfo.GetVersionInfo(exe); it.Publisher = Launchers.Contains(Path.GetFileNameWithoutExtension(exe)) ? null : v.CompanyName; it.Product = !string.IsNullOrWhiteSpace(v.ProductName) ? v.ProductName : v.FileDescription; } catch { }
        }
        if (!string.IsNullOrWhiteSpace(it.Product) && it.ExePath != null && !Launchers.Contains(Path.GetFileNameWithoutExtension(it.ExePath)) && !it.Product.Contains("Operating System", StringComparison.OrdinalIgnoreCase)) it.Name = it.Product!.Replace("®", "").Replace("™", "").Trim() is { Length: > 0 } n ? n : it.Name;
    }

    /// <summary>Pulls the program path out of a command line such as "C:\Program Files\X\x.exe" --background.</summary>
    public static string? ExtractExe(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim()); if (cmd.Length == 0) return null;
        if (cmd[0] == '"') { int e = cmd.IndexOf('"', 1); return e > 1 ? cmd[1..e] : null; }
        int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (i > 0) return cmd[..(i + 4)];
        return cmd.Contains(' ') ? cmd[..cmd.IndexOf(' ')] : cmd;
    }

    /// <summary>Turns an entry on or off. Returns (ok, message).</summary>
    public static (bool ok, string message) SetEnabled(StartupItem it, bool enable)
    {
        if (it.NeedsAdmin && !Elevation.IsElevated()) return (false, "This program starts for every user, so changing it needs administrator rights. Use “Restart as administrator”.");
        try
        {
            using var bk = RegistryKey.OpenBaseKey(it.Hive, RegistryView.Registry64);
            using var k = bk.CreateSubKey(Appr + it.ApprovedKey, true);
            var data = new byte[12];
            if (enable) data[0] = 2; else { data[0] = 3; BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4); }
            k.SetValue(it.ValueName, data, RegistryValueKind.Binary);
            bool now = IsEnabled(ReadApproved(it.Hive, it.ApprovedKey, it.ValueName));
            if (now != enable) return (false, "Windows did not keep the change.");
            it.Enabled = now;
            return (true, (enable ? "Enabled: it will start at your next sign-in." : "Disabled: it will not start at your next sign-in. Nothing was deleted; you can turn it back on here.") );
        }
        catch (UnauthorizedAccessException) { return (false, "Windows denied access. Restart HardwareLens as administrator to change this entry."); }
        catch (System.Security.SecurityException) { return (false, "Windows denied access. Restart HardwareLens as administrator to change this entry."); }
        catch (Exception e) { return (false, "Could not change it: " + e.Message); }
    }

    public static void ShowInFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); } catch { }
    }
}
