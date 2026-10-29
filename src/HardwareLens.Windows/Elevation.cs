using System.Diagnostics;
using System.Security.Principal;
namespace HardwareLens.Windows;

public static class Elevation
{
    public static bool IsElevated()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; }
    }

    /// <summary>Starts a new elevated copy of this program (Windows shows its normal UAC prompt). Returns false if the user declined.</summary>
    public static bool StartElevatedCopy()
    {
        try
        {
            string? exe = Environment.ProcessPath; if (exe == null) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch { return false; }
    }
}
