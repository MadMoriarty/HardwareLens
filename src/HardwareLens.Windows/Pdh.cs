using System.Runtime.InteropServices;
namespace HardwareLens.Windows;

/// <summary>Thin wrapper over the documented Performance Data Helper (PDH) API using locale-independent (English) counter paths.</summary>
internal sealed unsafe class PdhQuery : IDisposable
{
    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? src, nint user, out nint q);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(nint q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(nint q, string path, nint user, out nint counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(nint q);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufSize, out uint itemCount, nint buffer);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Item { [FieldOffset(0)] public nint Name; [FieldOffset(8)] public uint CStatus; [FieldOffset(16)] public double Value; }

    nint query;
    public PdhQuery() { uint r = PdhOpenQueryW(null, 0, out query); if (r != 0) throw new InvalidOperationException("PdhOpenQuery failed 0x" + r.ToString("X8")); }

    public sealed class Counter
    {
        internal nint Handle; public string Path = ""; public uint AddStatus; public bool Ok => Handle != 0;
        internal nint Buffer; internal uint BufferSize;
    }

    public Counter Add(string englishPath)
    {
        var c = new Counter { Path = englishPath };
        c.AddStatus = PdhAddEnglishCounterW(query, englishPath, 0, out c.Handle);
        if (c.AddStatus != 0) c.Handle = 0;
        return c;
    }

    /// <summary>Collects one sample for every counter in the query. Rate counters return data from the second call onward.</summary>
    public uint Collect() => PdhCollectQueryData(query);

    /// <summary>All instances of a (possibly wildcard) counter. Instances with an invalid status are skipped.</summary>
    public bool Read(Counter c, List<KeyValuePair<string, double>> into, out uint status)
    {
        into.Clear(); status = 0;
        if (!c.Ok) { status = c.AddStatus; return false; }
        uint size = c.BufferSize, count;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            status = PdhGetFormattedCounterArrayW(c.Handle, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, c.Buffer);
            if (status == PDH_MORE_DATA)
            {
                if (c.Buffer != 0) Marshal.FreeHGlobal(c.Buffer);
                c.BufferSize = size + 4096; c.Buffer = Marshal.AllocHGlobal((nint)c.BufferSize); size = c.BufferSize; continue;
            }
            if (status != 0) return false;
            var items = (Item*)c.Buffer;
            for (uint i = 0; i < count; i++)
            {
                if (items[i].CStatus != 0 && items[i].CStatus != 1) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA
                string? name = Marshal.PtrToStringUni(items[i].Name);
                if (name != null) into.Add(new(name, items[i].Value));
            }
            return true;
        }
        return false;
    }

    public static string Describe(uint status) => status switch
    {
        0xC0000BB8 => "counter object not found on this system (0xC0000BB8)",
        0xC0000BB9 => "counter not found (0xC0000BB9)",
        0x800007D5 => "no data yet (PDH_NO_DATA)",
        0x800007D2 => "buffer too small",
        0xC0000BC6 => "invalid data (PDH_INVALID_DATA)",
        _ => "PDH status 0x" + status.ToString("X8")
    };

    public void Dispose()
    {
        if (query != 0) { PdhCloseQuery(query); query = 0; }
    }
}
