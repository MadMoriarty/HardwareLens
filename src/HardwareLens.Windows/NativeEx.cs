using System.Runtime.InteropServices;
using System.Text;
namespace HardwareLens.Windows;

internal static class NativeEx
{
    internal const uint PROCESS_TERMINATE = 0x0001, PROCESS_SET_INFORMATION = 0x0200, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x00100000;
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageNameW(nint h, uint flags, StringBuilder sb, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessTimes(nint h, out long create, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsProcessCritical(nint h, out bool critical);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateProcess(nint h, uint code);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(nint h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetPriorityClass(nint h, uint cls);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetPriorityClass(nint h);

    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint h, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(nint h, StringBuilder sb, int max);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLengthW(nint h);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessageW(nint h, uint msg, nint w, nint l);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint h, int attr, out int value, int size);
    internal const uint WM_CLOSE = 0x0010, GW_OWNER = 4; internal const int GWL_EXSTYLE = -20; internal const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

    /// <summary>Top-level windows as (hwnd, pid, title, visibleAppWindow).</summary>
    internal static List<(nint hwnd, int pid, string title, bool appWindow)> TopLevelWindows()
    {
        var res = new List<(nint, int, string, bool)>();
        EnumWindows((h, _) =>
        {
            try
            {
                GetWindowThreadProcessId(h, out int pid);
                bool vis = IsWindowVisible(h); bool app = false; string title = "";
                if (vis)
                {
                    long ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE); bool owned = GetWindow(h, GW_OWNER) != 0;
                    DwmGetWindowAttribute(h, 14, out int cloaked, 4);
                    int len = GetWindowTextLengthW(h);
                    if (len > 0) { var sb = new StringBuilder(len + 1); GetWindowTextW(h, sb, sb.Capacity); title = sb.ToString(); }
                    app = cloaked == 0 && title.Length > 0 && !owned && ((ex & WS_EX_TOOLWINDOW) == 0 || (ex & WS_EX_APPWINDOW) != 0);
                }
                res.Add((h, pid, title, app));
            }
            catch { }
            return true;
        }, 0);
        return res;
    }

    internal static string? ImagePath(nint h)
    {
        var sb = new StringBuilder(1024); uint n = (uint)sb.Capacity;
        return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : null;
    }
}
