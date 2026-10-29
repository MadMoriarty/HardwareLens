using System.Runtime.InteropServices;
namespace HardwareLens.Windows;

internal sealed record DxgiAdapter(string Description, uint VendorId, uint DeviceId, ulong DedicatedVideoMemory, ulong DedicatedSystemMemory, ulong SharedSystemMemory, string LuidKey, bool IsSoftware, bool? IsUma, string? Note);

/// <summary>Enumerates graphics adapters through DXGI (documented) and asks Direct3D 12 whether each adapter uses a unified memory architecture (integrated GPU) via CheckFeatureSupport(D3D12_FEATURE_ARCHITECTURE).</summary>
internal static unsafe class DxgiProbe
{
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(Guid* riid, void** factory);
    [DllImport("d3d12.dll")] static extern int D3D12CreateDevice(void* adapter, int minFeatureLevel, Guid* riid, void** device);

    [StructLayout(LayoutKind.Sequential)]
    struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] struct D3D12_FEATURE_DATA_ARCHITECTURE { public uint NodeIndex; public int TileBasedRenderer, UMA, CacheCoherentUMA; }

    static void** Vt(void* o) => *(void***)o;
    static void Release(void* o) { if (o != null) ((delegate* unmanaged<void*, uint>)Vt(o)[2])(o); }

    public static List<DxgiAdapter> Enumerate(out string? error)
    {
        var list = new List<DxgiAdapter>(); error = null;
        void* factory = null;
        try
        {
            var iidFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            int hr = CreateDXGIFactory1(&iidFactory1, &factory);
            if (hr < 0) { error = "CreateDXGIFactory1 failed 0x" + hr.ToString("X8"); return list; }
            for (uint i = 0; ; i++)
            {
                void* adapter = null;
                int r = ((delegate* unmanaged<void*, uint, void**, int>)Vt(factory)[12])(factory, i, &adapter); // IDXGIFactory1::EnumAdapters1
                if (r < 0 || adapter == null) break;
                try
                {
                    DXGI_ADAPTER_DESC1 d = default;
                    if (((delegate* unmanaged<void*, DXGI_ADAPTER_DESC1*, int>)Vt(adapter)[10])(adapter, &d) < 0) continue; // IDXGIAdapter1::GetDesc1
                    string name = new string(d.Description).TrimEnd('\0', ' ');
                    bool software = (d.Flags & 2) != 0;
                    bool? uma = null; string? note = null;
                    if (!software)
                    {
                        try
                        {
                            void* dev = null; var iidDev = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");
                            int h2 = D3D12CreateDevice(adapter, 0xb000, &iidDev, &dev);
                            if (h2 >= 0 && dev != null)
                            {
                                var arch = new D3D12_FEATURE_DATA_ARCHITECTURE();
                                int h3 = ((delegate* unmanaged<void*, int, void*, uint, int>)Vt(dev)[13])(dev, 2, &arch, (uint)sizeof(D3D12_FEATURE_DATA_ARCHITECTURE)); // ID3D12Device::CheckFeatureSupport(ARCHITECTURE)
                                if (h3 >= 0) uma = arch.UMA != 0; else note = "Direct3D 12 architecture query failed (0x" + h3.ToString("X8") + ")";
                                Release(dev);
                            }
                            else note = "Direct3D 12 device could not be created (0x" + h2.ToString("X8") + ")";
                        }
                        catch (DllNotFoundException) { note = "Direct3D 12 is not available"; }
                        catch (EntryPointNotFoundException) { note = "Direct3D 12 is not available"; }
                    }
                    list.Add(new DxgiAdapter(name, d.VendorId, d.DeviceId, d.DedicatedVideoMemory, d.DedicatedSystemMemory, d.SharedSystemMemory, $"0x{(uint)d.LuidHigh:X8}_0x{d.LuidLow:X8}", software, uma, note));
                }
                finally { Release(adapter); }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { error = "DXGI not available: " + e.Message; }
        finally { Release(factory); }
        return list;
    }
}
