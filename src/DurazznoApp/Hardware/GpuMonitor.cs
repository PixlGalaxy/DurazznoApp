using System.Runtime.InteropServices;
using Durazzno.Core;

namespace Durazzno.Hardware;

/// <summary>One GPU's live numbers. Temperature and memory are null when the driver does not report them.</summary>
internal sealed record GpuSample(string Name, double Usage, double? TemperatureC, ulong? MemoryUsed, ulong? MemoryTotal);

/// <summary>
/// GPU usage, temperature and dedicated memory, read the way Task Manager reads them, so a sleeping laptop dGPU is not
/// woken up (NVML / nvidia-smi would wake it).
/// - Usage: PDH "GPU Engine(*)\Utilization Percentage". Each engine's usage is summed over processes, and the GPU's
///   usage is its busiest engine.
/// - Memory: PDH "GPU Adapter Memory(*)\Dedicated Usage" against the dedicated size from D3DKMT; integrated GPUs use
///   "Shared Usage" against their shared system memory.
/// - Temperature: D3DKMTQueryAdapterInfo(ADAPTERPERFDATA), reported in tenths of a degree by WDDM 2.4+ drivers.
/// </summary>
internal sealed class GpuMonitor : IDisposable
{
    /// <summary>Integrated GPUs (little or no dedicated memory) report their shared system memory instead.</summary>
    private sealed record Adapter(uint Handle, string LuidKey, string Name, ulong MemoryBytes, bool Shared);

    private const ulong MinDedicated = 512UL * 1024 * 1024;

    private readonly List<Adapter> _adapters = [];
    private IntPtr _query;
    private IntPtr _engineCounter;
    private IntPtr _memoryCounter;
    private IntPtr _sharedCounter;

    public GpuMonitor()
    {
        try
        {
            EnumerateAdapters();
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
            {
                PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engineCounter);
                PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _memoryCounter);
                PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Shared Usage", IntPtr.Zero, out _sharedCounter);
                PdhCollectQueryData(_query); // usage needs two samples: this is the first
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"GPU monitor: {ex.Message}");
        }
    }

    /// <summary>The GPUs found (hardware only, no Microsoft Basic Render Driver).</summary>
    public IReadOnlyList<string> Names => _adapters.Select(a => a.Name).ToList();

    public List<GpuSample> Sample()
    {
        var usage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var memory = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        if (_query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
        {
            // Instance: pid_1234_luid_0x00000000_0x0000D1C5_phys_0_eng_3_engtype_3D
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (instance, value) in Read(_engineCounter))
            {
                var at = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                var typeAt = instance.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
                if (at < 0 || typeAt < at)
                    continue;
                var engine = instance[at..typeAt];
                engines[engine] = engines.GetValueOrDefault(engine) + value;
            }
            foreach (var (engine, value) in engines)
            {
                var luid = LuidOf(engine);
                usage[luid] = Math.Max(usage.GetValueOrDefault(luid), Math.Min(100, value));
            }
            // Instance: luid_0x00000000_0x0000D1C5_phys_0
            foreach (var (instance, value) in Read(_memoryCounter))
            {
                var luid = LuidOf(instance);
                memory[luid] = memory.GetValueOrDefault(luid) + (ulong)Math.Max(0, value);
            }
            foreach (var (instance, value) in Read(_sharedCounter))
            {
                var luid = LuidOf(instance) + "|shared";
                memory[luid] = memory.GetValueOrDefault(luid) + (ulong)Math.Max(0, value);
            }
        }

        return _adapters.Select(a => new GpuSample(
            a.Name,
            usage.GetValueOrDefault(a.LuidKey),
            Temperature(a.Handle),
            memory.TryGetValue(a.Shared ? a.LuidKey + "|shared" : a.LuidKey, out var used) ? used : null,
            a.MemoryBytes > 0 ? a.MemoryBytes : null)).ToList();
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
            PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        foreach (var a in _adapters)
        {
            var close = new D3DKMT_CLOSEADAPTER { hAdapter = a.Handle };
            D3DKMTCloseAdapter(ref close);
        }
        _adapters.Clear();
    }

    // "luid_0x00000000_0x0000D1C5_phys_0_eng_3" -> "luid_0x00000000_0x0000d1c5"
    private static string LuidOf(string text)
    {
        var at = text.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return text;
        var parts = text[at..].Split('_');
        return parts.Length >= 3 ? $"luid_{parts[1]}_{parts[2]}".ToLowerInvariant() : text.ToLowerInvariant();
    }

    private void EnumerateAdapters()
    {
        var enumAdapters = new D3DKMT_ENUMADAPTERS2();
        if (D3DKMTEnumAdapters2(ref enumAdapters) != 0 || enumAdapters.NumAdapters == 0)
            return;
        var friendly = FriendlyNames();
        var size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
        enumAdapters.pAdapters = Marshal.AllocHGlobal(size * (int)enumAdapters.NumAdapters);
        try
        {
            if (D3DKMTEnumAdapters2(ref enumAdapters) != 0)
                return;
            for (var i = 0; i < enumAdapters.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(enumAdapters.pAdapters + i * size);
                if (IsSoftware(info.hAdapter) || Name(info.hAdapter) is not { } name)
                {
                    var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                    D3DKMTCloseAdapter(ref close);
                    continue;
                }
                var luid = $"luid_0x{info.AdapterLuid.HighPart:x8}_0x{info.AdapterLuid.LowPart:x8}";
                // The driver's adapter string can be a codename ("RaptorLake-S Mobile Graphics Controller"):
                // prefer the Device Manager name, matched on vendor + device ID.
                if (Query<D3DKMT_QUERY_DEVICE_IDS>(info.hAdapter, KMTQAITYPE_PHYSICALADAPTERDEVICEIDS, out var ids)
                    && friendly.TryGetValue($"VEN_{ids.VendorID:X4}&DEV_{ids.DeviceID:X4}", out var pretty))
                    name = Clean(pretty);
                var segments = Query<D3DKMT_SEGMENTSIZEINFO>(info.hAdapter, KMTQAITYPE_GETSEGMENTSIZE, out var seg) ? seg : default;
                var shared = segments.DedicatedVideoMemorySize < MinDedicated;
                _adapters.Add(new Adapter(info.hAdapter, luid, name,
                    shared ? segments.SharedSystemMemorySize : segments.DedicatedVideoMemorySize, shared));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(enumAdapters.pAdapters);
        }
    }

    private static bool Query<T>(uint adapter, int type, out T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(default(T), buffer, false);
            var query = new D3DKMT_QUERYADAPTERINFO { hAdapter = adapter, Type = type, pPrivateDriverData = buffer, PrivateDriverDataSize = (uint)size };
            var ok = D3DKMTQueryAdapterInfo(ref query) == 0;
            value = ok ? Marshal.PtrToStructure<T>(buffer) : default;
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsSoftware(uint adapter) =>
        Query<uint>(adapter, KMTQAITYPE_ADAPTERTYPE, out var flags) && (flags & 0x4) != 0; // SoftwareDevice bit

    private static string? Name(uint adapter) =>
        Query<D3DKMT_ADAPTERREGISTRYINFO>(adapter, KMTQAITYPE_ADAPTERREGISTRYINFO, out var info) && info.AdapterString is { Length: > 0 } s
            ? Clean(s)
            : null;

    private static string Clean(string name) => name.Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ").Trim();

    /// <summary>Win32_VideoController names keyed by "VEN_xxxx&amp;DEV_yyyy".</summary>
    private static Dictionary<string, string> FriendlyNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
            foreach (var o in searcher.Get())
            {
                if (o["Name"] is not string name || o["PNPDeviceID"] is not string id)
                    continue;
                var m = System.Text.RegularExpressions.Regex.Match(id, @"VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                    names.TryAdd(m.Value, name);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"GPU names: {ex.Message}");
        }
        return names;
    }

    private static double? Temperature(uint adapter)
    {
        if (!Query<D3DKMT_ADAPTER_PERFDATA>(adapter, KMTQAITYPE_ADAPTERPERFDATA, out var perf))
            return null;
        var celsius = perf.Temperature / 10.0;
        return celsius is > 0 and < 150 ? celsius : null; // 0 = not reported (or the GPU is asleep)
    }

    private static List<(string Instance, double Value)> Read(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero)
            return list;
        uint bytes = 0, count = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, ref count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || bytes == 0)
            return list;
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, ref count, buffer) != 0)
                return list;
            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(buffer + i * itemSize);
                if (item.FmtValue.CStatus == 0 && Marshal.PtrToStringUni(item.szName) is { } name)
                    list.Add((name, item.FmtValue.doubleValue));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    // â”€â”€ Native â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private const int KMTQAITYPE_GETSEGMENTSIZE = 3;
    private const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
    private const int KMTQAITYPE_ADAPTERTYPE = 15;
    private const int KMTQAITYPE_PHYSICALADAPTERDEVICEIDS = 31;
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct D3DKMT_ADAPTERREGISTRYINFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_SEGMENTSIZEINFO
    {
        public ulong DedicatedVideoMemorySize;
        public ulong DedicatedSystemMemorySize;
        public ulong SharedSystemMemorySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERY_DEVICE_IDS
    {
        public uint PhysicalAdapterIndex;
        public uint VendorID;
        public uint DeviceID;
        public uint SubVendorID;
        public uint SubSystemID;
        public uint RevisionID;
        public uint BusType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue; // union: only the double is used
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 enumAdapters);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO query);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER close);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
