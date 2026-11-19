using System.IO;
using System.Runtime.InteropServices;

namespace DotLight;

/// <summary>Small Windows calls for the fun features. Each one is cheap and only runs while its feature is on.</summary>
static class FunNative
{
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    /// <summary>Tick count of the last key press or mouse input anywhere (0 if unknown). Says when, never what.</summary>
    public static uint LastInput()
    {
        var i = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref i) ? i.dwTime : 0;
    }

    /// <summary>Seconds since the last key press or mouse input.</summary>
    public static double IdleSeconds()
    {
        uint last = LastInput();
        return last == 0 ? 0 : unchecked((uint)Environment.TickCount - last) / 1000.0;
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Moves a window without resizing, re-ordering or activating it.</summary>
    public static void MoveWindow(IntPtr hWnd, int x, int y) => SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);

    public static bool CtrlDown => (GetAsyncKeyState(0x11) & 0x8000) != 0;
    public static bool LeftButtonDown => (GetAsyncKeyState(0x01) & 0x8000) != 0;
}

/// <summary>How busy the PC is: CPU from GetSystemTimes, GPU from the "GPU Engine" counters (the same ones Task Manager reads).</summary>
sealed class SystemLoad : IDisposable
{
    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr items);

    [DllImport("pdh.dll")]
    static extern int PdhCloseQuery(IntPtr query);

    const uint PDH_FMT_DOUBLE = 0x200;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    long lastIdle, lastTotal;
    IntPtr query, gpu;
    IntPtr buffer;
    uint bufferBytes;

    public SystemLoad()
    {
        Cpu();
        if (PdhOpenQuery(null, IntPtr.Zero, out query) == 0 &&
            PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out gpu) == 0)
            PdhCollectQueryData(query); // rates need two samples: this is the first
        else gpu = IntPtr.Zero;
    }

    /// <summary>CPU use since the last call, 0 to 100.</summary>
    public double Cpu()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
        long total = kernel + user; // kernel time includes idle time
        long dIdle = idle - lastIdle, dTotal = total - lastTotal;
        lastIdle = idle; lastTotal = total;
        return dTotal <= 0 ? 0 : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }

    /// <summary>GPU use, 0 to 100: the busiest engine (3D, video, copy...), summed over the apps using it. Null if Windows has no GPU counters.</summary>
    public double? Gpu()
    {
        if (gpu == IntPtr.Zero || PdhCollectQueryData(query) != 0) return null;
        uint bytes = bufferBytes;
        int r = PdhGetFormattedCounterArray(gpu, PDH_FMT_DOUBLE, ref bytes, out uint count, buffer);
        if (r == PDH_MORE_DATA)
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            bufferBytes = bytes + 4096;
            buffer = Marshal.AllocHGlobal((int)bufferBytes);
            bytes = bufferBytes;
            r = PdhGetFormattedCounterArray(gpu, PDH_FMT_DOUBLE, ref bytes, out count, buffer);
        }
        if (r != 0) return null;

        // Each item: name pointer, then { status, value }. Names look like
        // "pid_1234_luid_0x0_0x1_phys_0_eng_3_engtype_3D"; add up per engine, keep the busiest engine.
        var perEngine = new Dictionary<string, double>();
        int size = IntPtr.Size + 16;
        for (int i = 0; i < count; i++)
        {
            IntPtr item = buffer + i * size;
            if (Marshal.ReadInt32(item + IntPtr.Size) > 1) continue; // not valid data
            double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
            string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
            int at = name.IndexOf("_luid", StringComparison.Ordinal);
            string engine = at >= 0 ? name[at..] : name;
            perEngine[engine] = perEngine.GetValueOrDefault(engine) + v;
        }
        return perEngine.Count == 0 ? 0 : Math.Clamp(perEngine.Values.Max(), 0, 100);
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; gpu = IntPtr.Zero; }
        if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
    }
}

/// <summary>
/// Notices when the Recycle Bin is emptied. Watches the bin folders for changes (no polling), then asks
/// Windows once how many items are left.
/// </summary>
sealed class RecycleBinWatch : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    readonly List<FileSystemWatcher> watchers = new();
    readonly System.Threading.Timer settle;
    long items;

    /// <summary>Raised on a background thread when the bin goes from some items to none.</summary>
    public event Action? Emptied;

    public RecycleBinWatch()
    {
        items = Count();
        settle = new System.Threading.Timer(_ => Check());
        string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null) return;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string dir = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid);
                if (!Directory.Exists(dir)) continue;
                var w = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 4096 };
                w.Deleted += (_, _) => settle.Change(1200, System.Threading.Timeout.Infinite);
                w.Created += (_, _) => settle.Change(1200, System.Threading.Timeout.Infinite);
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch { /* a drive we can't watch: skip it */ }
        }
    }

    static long Count()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64NumItems : -1;
    }

    void Check()
    {
        long now = Count();
        if (now < 0) return;
        bool emptied = items > 0 && now == 0;
        items = now;
        if (emptied) Emptied?.Invoke();
    }

    public void Dispose()
    {
        foreach (var w in watchers) w.Dispose();
        watchers.Clear();
        settle.Dispose();
    }
}

/// <summary>
/// CPU and GPU temperatures without a driver or admin rights. CPU: Windows' ACPI thermal zones (some PCs
/// don't report one). GPU: the same reading Task Manager shows, for graphics cards that report it.
/// </summary>
sealed class Temperatures : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")]
    static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr items);
    [DllImport("pdh.dll")]
    static extern int PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
    [StructLayout(LayoutKind.Sequential)]
    struct QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pData; public uint DataSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct CLOSEADAPTER { public uint hAdapter; }

    [DllImport("gdi32.dll")]
    static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 e);
    [DllImport("gdi32.dll")]
    static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO q);
    [DllImport("gdi32.dll")]
    static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER c);

    const int KMTQAITYPE_ADAPTERPERFDATA = 62;
    const int AdapterInfoSize = 20, PerfDataSize = 64, PerfTemperatureAt = 56;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    IntPtr query, zones;
    readonly List<uint> adapters = new();

    public Temperatures()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0 ||
            PdhAddEnglishCounter(query, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out zones) != 0)
            zones = IntPtr.Zero;

        try
        {
            var e = new ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return;
            e.pAdapters = Marshal.AllocHGlobal(AdapterInfoSize * (int)e.NumAdapters);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) == 0)
                    for (int i = 0; i < e.NumAdapters; i++) adapters.Add((uint)Marshal.ReadInt32(e.pAdapters + i * AdapterInfoSize));
            }
            finally { Marshal.FreeHGlobal(e.pAdapters); }
        }
        catch { /* older Windows: no GPU temperature */ }
    }

    /// <summary>The warmest thermal zone, in °C, or null when this PC doesn't report one.</summary>
    public double? Cpu()
    {
        if (zones == IntPtr.Zero || PdhCollectQueryData(query) != 0) return null;
        uint bytes = 0;
        if (PdhGetFormattedCounterArray(zones, 0x200, ref bytes, out _, IntPtr.Zero) != PDH_MORE_DATA) return null;
        var buf = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArray(zones, 0x200, ref bytes, out uint count, buf) != 0) return null;
            double? best = null;
            int size = IntPtr.Size + 16;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buf + i * size;
                if (Marshal.ReadInt32(item + IntPtr.Size) > 1) continue;
                double c = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8)) - 273.15; // Kelvin
                if (c is > 5 and < 125 && (best == null || c > best)) best = c;
            }
            return best;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The warmest graphics card that reports a temperature, in °C, or null.</summary>
    public double? Gpu()
    {
        double? best = null;
        var data = Marshal.AllocHGlobal(PerfDataSize);
        try
        {
            foreach (var a in adapters)
            {
                for (int i = 0; i < PerfDataSize; i++) Marshal.WriteByte(data, i, 0);
                var q = new QUERYADAPTERINFO { hAdapter = a, Type = KMTQAITYPE_ADAPTERPERFDATA, pData = data, DataSize = PerfDataSize };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) continue;
                double c = Marshal.ReadInt32(data + PerfTemperatureAt) / 10.0; // tenths of a degree
                if (c is > 5 and < 125 && (best == null || c > best)) best = c;
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(data); }
        return best;
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; zones = IntPtr.Zero; }
        foreach (var a in adapters) { var c = new CLOSEADAPTER { hAdapter = a }; D3DKMTCloseAdapter(ref c); }
        adapters.Clear();
    }
}
