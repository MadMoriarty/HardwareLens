using System.Runtime.InteropServices;
using HardwareLens.Core;
namespace HardwareLens.Windows;

public enum EndResult { Closed, Terminated, Refused, AccessDenied, NotFound, StillRunning, Failed }
public sealed record EndOutcome(int Pid, string Name, EndResult Result, string Detail)
{
    public bool Success => Result is EndResult.Closed or EndResult.Terminated;
}

/// <summary>Ends tasks and changes priority. Every action re-verifies the process identity (PID + start time) first, so a reused PID is never hit by mistake.</summary>
public static class ProcessControl
{
    static readonly HashSet<string> NeverEnd = new(StringComparer.OrdinalIgnoreCase) { "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "Secure System", "Memory Compression", "dwm" };

    public static string? WhyNotEnd(int pid, string name, bool critical)
    {
        if (pid <= 4) return "This is a core Windows process. Ending it would crash Windows.";
        if (pid == Environment.ProcessId) return "That is HardwareLens itself. Use the close button instead.";
        if (critical || NeverEnd.Contains(AppGrouper.ExeName(name))) return "Windows marks this process as critical. Ending it would crash or restart the PC, so HardwareLens will not do it.";
        return null;
    }

    public static EndOutcome EndTask(ProcessSample p, bool force, int graceMs = 3000)
    {
        string name = p.Name;
        nint h = NativeEx.OpenProcess(NativeEx.PROCESS_TERMINATE | NativeEx.PROCESS_QUERY_LIMITED_INFORMATION | NativeEx.SYNCHRONIZE, false, p.Pid);
        if (h == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 87) return new(p.Pid, name, EndResult.NotFound, "Already gone.");
            if (err == 5) return new(p.Pid, name, EndResult.AccessDenied, "Windows does not allow a standard user to end this process. Restart HardwareLens as administrator to try.");
            return new(p.Pid, name, EndResult.Failed, "Could not open the process (Windows error " + err + ").");
        }
        try
        {
            if (NativeEx.GetProcessTimes(h, out long create, out _, out _, out _) && create != p.StartKey) return new(p.Pid, name, EndResult.NotFound, "That program already exited and its number was reused; nothing was ended.");
            bool crit = false; NativeEx.IsProcessCritical(h, out crit);
            var refuse = WhyNotEnd(p.Pid, name, crit); if (refuse != null) return new(p.Pid, name, EndResult.Refused, refuse);
            if (NativeEx.WaitForSingleObject(h, 0) == 0) return new(p.Pid, name, EndResult.Closed, "Already exited.");

            var wins = NativeEx.TopLevelWindows().Where(w => w.pid == p.Pid).ToList();
            if (!force && wins.Count > 0)
            {
                foreach (var w in wins) NativeEx.PostMessageW(w.hwnd, NativeEx.WM_CLOSE, 0, 0);
                if (NativeEx.WaitForSingleObject(h, (uint)graceMs) == 0) return new(p.Pid, name, EndResult.Closed, "Closed normally.");
                return new(p.Pid, name, EndResult.StillRunning, "The program did not close by itself. It may be asking whether to save, or it may be frozen.");
            }
            if (!NativeEx.TerminateProcess(h, 1))
            {
                int err = Marshal.GetLastWin32Error();
                return err == 5 ? new(p.Pid, name, EndResult.AccessDenied, "Access denied. Restart HardwareLens as administrator to end this one.") : new(p.Pid, name, EndResult.Failed, "Windows refused (error " + err + ").");
            }
            NativeEx.WaitForSingleObject(h, 2000);
            return new(p.Pid, name, EndResult.Terminated, force ? "Force-ended." : "Ended.");
        }
        finally { NativeEx.CloseHandle(h); }
    }

    public static readonly (string label, uint cls)[] Priorities =
    {
        ("Low (idle)", 0x40), ("Below normal", 0x4000), ("Normal", 0x20), ("Above normal", 0x8000), ("High", 0x80),
    };

    public static string? CurrentPriority(int pid)
    {
        nint h = NativeEx.OpenProcess(NativeEx.PROCESS_QUERY_LIMITED_INFORMATION, false, pid); if (h == 0) return null;
        try { uint c = NativeEx.GetPriorityClass(h); foreach (var (l, k) in Priorities) if (k == c) return l; return c == 0x100 ? "Realtime" : null; } finally { NativeEx.CloseHandle(h); }
    }

    public static (bool ok, string detail) SetPriority(ProcessSample p, uint cls)
    {
        nint h = NativeEx.OpenProcess(NativeEx.PROCESS_SET_INFORMATION | NativeEx.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
        if (h == 0) return (false, Marshal.GetLastWin32Error() == 5 ? "Windows does not allow changing this program's priority without administrator rights." : "Could not open the process.");
        try
        {
            if (NativeEx.GetProcessTimes(h, out long create, out _, out _, out _) && create != p.StartKey) return (false, "That program already exited.");
            return NativeEx.SetPriorityClass(h, cls) ? (true, "Priority changed. It lasts until the program restarts.") : (false, "Windows refused the change.");
        }
        finally { NativeEx.CloseHandle(h); }
    }
}
