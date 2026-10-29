using System.Runtime.InteropServices;
namespace HardwareLens.Windows;

internal static unsafe class Native
{
    // ---------- Memory ----------
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("psapi.dll", SetLastError = true)] internal static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pi, uint cb);

    [DllImport("kernel32.dll")] internal static extern uint GetActiveProcessorCount(ushort group);

    // ---------- Power ----------
    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    // ---------- Process snapshot (NtQuerySystemInformation, the call .NET's Process class itself uses) ----------
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, void* buffer, int length, out int returnLength);

    internal struct RawProcess
    {
        public int Pid, Threads, Handles;
        public long CreateTime;
        public ulong CpuTime100ns, ReadBytes, WriteBytes;
        public long WorkingSet, PrivateWorkingSet, Commit;
        public string Name;
    }

    /// <summary>Returns false (with a reason) when the call or the structure sanity checks fail.</summary>
    internal static bool TrySnapshotProcesses(ref byte[]? scratch, List<RawProcess> into, out string? error)
    {
        into.Clear(); error = null;
        const int SystemProcessInformation = 5; const int InfoLengthMismatch = unchecked((int)0xC0000004), BufferTooSmall = unchecked((int)0xC0000023);
        scratch ??= new byte[1 << 20];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int status, needed;
            fixed (byte* p = scratch) status = NtQuerySystemInformation(SystemProcessInformation, p, scratch.Length, out needed);
            if (status == InfoLengthMismatch || status == BufferTooSmall) { scratch = new byte[Math.Max(needed + (64 << 10), scratch.Length * 2)]; continue; }
            if (status < 0) { error = "NtQuerySystemInformation failed with NTSTATUS 0x" + status.ToString("X8"); return false; }
            fixed (byte* basePtr = scratch)
            {
                long off = 0;
                while (true)
                {
                    if (off < 0 || off + 256 > scratch.Length) { error = "Process list layout check failed (offset out of range)."; into.Clear(); return false; }
                    byte* e = basePtr + off;
                    uint next = *(uint*)e; uint threads = *(uint*)(e + 4);
                    var rp = new RawProcess
                    {
                        Threads = (int)threads,
                        PrivateWorkingSet = *(long*)(e + 8),
                        CreateTime = *(long*)(e + 32),
                        CpuTime100ns = (ulong)(*(long*)(e + 40)) + (ulong)(*(long*)(e + 48)),
                        Pid = (int)*(long*)(e + 80),
                        Handles = (int)*(uint*)(e + 96),
                        WorkingSet = (long)*(ulong*)(e + 144),
                        Commit = (long)*(ulong*)(e + 200),
                        ReadBytes = *(ulong*)(e + 232),
                        WriteBytes = *(ulong*)(e + 240),
                    };
                    ushort nameLen = *(ushort*)(e + 56); char* namePtr = *(char**)(e + 64);
                    rp.Name = nameLen > 0 && namePtr != null && nameLen <= 520 ? new string(namePtr, 0, nameLen / 2) : (rp.Pid == 0 ? "System Idle Process" : "");
                    if (rp.Pid < 0 || threads > 100000) { error = "Process list layout check failed (implausible values)."; into.Clear(); return false; }
                    into.Add(rp);
                    if (next == 0) break;
                    if (next < 256) { error = "Process list layout check failed (bad entry size)."; into.Clear(); return false; }
                    off += next;
                }
            }
            return true;
        }
        error = "Process list buffer could not be sized."; return false;
    }
}
