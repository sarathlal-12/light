using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace DotLight;

/// <summary>
/// Keeps .light small while it sits idle: a full garbage collection, then hands the pages it isn't
/// using back to Windows. They come back on their own (in a few milliseconds) when the island is used.
/// </summary>
static class MemoryTrim
{
    static DateTime last = DateTime.MinValue;

    public static void Now()
    {
        if ((DateTime.Now - last).TotalSeconds < 10) return;
        last = DateTime.Now;
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var me = Process.GetCurrentProcess();
            EmptyWorkingSet(me.Handle);
        }
        catch { }
    }

    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);
}
