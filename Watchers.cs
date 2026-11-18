using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;
using WF = System.Windows.Forms;

namespace DotLight;

/// <summary>
/// Which apps are using the camera or microphone right now. Only reads, never changes anything.
/// Mic: the apps Windows' audio system lists as recording from a microphone right now.
/// Camera: the record Windows keeps for its privacy indicators (an app is "in use" while its stop time is
/// empty). Some recent Windows builds stopped writing that record; there the camera dot can't show.
/// </summary>
static class Privacy
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    public static (List<string> Camera, List<string> Mic) InUse()
    {
        var cam = new List<string>(); var mic = new List<string>();
        Scan("webcam", cam);
        Scan("microphone", mic);
        foreach (var app in Recording()) if (!mic.Contains(app, StringComparer.OrdinalIgnoreCase)) mic.Add(app);
        return (cam, mic);
    }

    // ---- microphone: active capture sessions on every microphone ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class DeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceEnumerator { [PreserveSig] int EnumAudioEndpoints(int flow, int state, out IDeviceCollection devices); }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceCollection { [PreserveSig] int GetCount(out uint n); [PreserveSig] int Item(uint i, out IDevice d); }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDevice { [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr a, int b, out IntPtr c);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr a, int b, out IntPtr c);
        [PreserveSig] int GetSessionEnumerator(out ISessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISessionEnumerator { [PreserveSig] int GetCount(out int n); [PreserveSig] int GetSession(int i, out ISessionControl2 s); }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr s);
        [PreserveSig] int SetDisplayName(IntPtr s, IntPtr g);
        [PreserveSig] int GetIconPath(out IntPtr s);
        [PreserveSig] int SetIconPath(IntPtr s, IntPtr g);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(IntPtr g, IntPtr c);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier(out IntPtr s);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr s);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    /// <summary>Names of the apps recording from any microphone right now.</summary>
    static List<string> Recording()
    {
        var names = new List<string>();
        try
        {
            var en = (IDeviceEnumerator)new DeviceEnumeratorCom();
            if (en.EnumAudioEndpoints(1 /* capture */, 1 /* active */, out var devices) != 0) return names;
            devices.GetCount(out uint count);
            var iid = typeof(ISessionManager2).GUID;
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out var dev) != 0) continue;
                if (dev.Activate(ref iid, 1 /* in-process */, IntPtr.Zero, out var o) != 0) continue;
                if (((ISessionManager2)o).GetSessionEnumerator(out var sessions) != 0) continue;
                sessions.GetCount(out int n);
                for (int s = 0; s < n; s++)
                {
                    if (sessions.GetSession(s, out var ctl) != 0) continue;
                    ctl.GetState(out int state);
                    ctl.GetProcessId(out uint pid);
                    if (state == 1 /* active */ && pid != 0 && pid != Environment.ProcessId)
                    {
                        try
                        {
                            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                            if (!names.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) names.Add(p.ProcessName);
                        }
                        catch { }
                    }
                    Marshal.ReleaseComObject(ctl);
                }
                Marshal.ReleaseComObject(sessions);
                Marshal.ReleaseComObject(o);
                Marshal.ReleaseComObject(dev);
            }
            Marshal.ReleaseComObject(devices);
            Marshal.ReleaseComObject(en);
        }
        catch { }
        return names;
    }

    static void Scan(string capability, List<string> into)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root + capability);
            if (root == null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                {
                    using var np = root.OpenSubKey(name);
                    if (np == null) continue;
                    foreach (var app in np.GetSubKeyNames()) Check(np, app, into);
                }
                else Check(root, name, into);
            }
        }
        catch { }
    }

    static void Check(RegistryKey parent, string name, List<string> into)
    {
        using var k = parent.OpenSubKey(name);
        if (k?.GetValue("LastUsedTimeStop") is long stop && stop == 0 &&
            k.GetValue("LastUsedTimeStart") is long start && start > 0)
        {
            var app = AppName(name);
            if (!into.Contains(app)) into.Add(app);
        }
    }

    /// <summary>"C:#Program Files#Zoom#bin#Zoom.exe" → "Zoom"; "Microsoft.WindowsCamera_8wekyb3d8bbwe" → "WindowsCamera".</summary>
    internal static string AppName(string key)
    {
        if (key.Contains('#')) return Path.GetFileNameWithoutExtension(key.Replace('#', '\\'));
        var pkg = key.Split('_')[0];
        return pkg[(pkg.LastIndexOf('.') + 1)..];
    }
}

/// <summary>Plugged in / battery low, for laptops. Quiet on PCs with no battery.</summary>
static class Power
{
    public static bool HasBattery => (WF.SystemInformation.PowerStatus.BatteryChargeStatus & WF.BatteryChargeStatus.NoSystemBattery) == 0
                                     && WF.SystemInformation.PowerStatus.BatteryChargeStatus != WF.BatteryChargeStatus.Unknown;
    public static bool Charging => WF.SystemInformation.PowerStatus.PowerLineStatus == WF.PowerLineStatus.Online;
    public static int Percent => (int)Math.Round(WF.SystemInformation.PowerStatus.BatteryLifePercent * 100);
}

/// <summary>Finished downloads: browsers save to a temporary name and rename it when the download is complete.</summary>
sealed class DownloadWatch : IDisposable
{
    static readonly string[] TempEnds = { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload" };
    readonly FileSystemWatcher? watcher;
    readonly Dictionary<string, DateTime> recent = new();
    public event Action<string>? Finished;

    public DownloadWatch()
    {
        var dir = Folder();
        if (dir == null || !Directory.Exists(dir)) return;
        watcher = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
        watcher.Renamed += (_, e) => { if (IsTemp(e.OldFullPath) && !IsTemp(e.FullPath)) Report(e.FullPath); };
        watcher.Created += async (_, e) =>
        {
            if (IsTemp(e.FullPath)) return;
            await Task.Delay(1500); // saved straight to its name: wait until it has something in it
            try { if (new FileInfo(e.FullPath).Length > 0) Report(e.FullPath); } catch { }
        };
        watcher.EnableRaisingEvents = true;
    }

    static bool IsTemp(string path) =>
        TempEnds.Any(t => path.EndsWith(t, StringComparison.OrdinalIgnoreCase)) ||
        Path.GetFileName(path).StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).StartsWith("~") || Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);

    void Report(string path)
    {
        lock (recent)
        {
            if (recent.TryGetValue(path, out var at) && DateTime.Now - at < TimeSpan.FromSeconds(5)) return;
            recent[path] = DateTime.Now;
        }
        if (File.Exists(path)) Finished?.Invoke(path);
    }

    /// <summary>Your Downloads folder, wherever you moved it.</summary>
    static string? Folder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p); return s;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public void Dispose() => watcher?.Dispose();
}

/// <summary>New .light versions, from the GitHub releases. Only checks; installs when you click Update.</summary>
static class Updates
{
    static UpdateManager? mgr;
    static UpdateInfo? found;

    static UpdateManager Manager => mgr ??= new UpdateManager(new GithubSource("https://github.com/sarathlal-12/light", null, false));

    /// <summary>The newer version's number, or null (also when this copy wasn't installed with the installer).</summary>
    public static async Task<string?> Check()
    {
        try
        {
            if (!Manager.IsInstalled) return null;
            found = await Manager.CheckForUpdatesAsync();
            return found?.TargetFullRelease.Version.ToString();
        }
        catch { return null; }
    }

    /// <summary>Downloads the update, then restarts into it. Returns false if it couldn't.</summary>
    public static async Task<bool> Install()
    {
        try
        {
            if (found == null) return false;
            await Manager.DownloadUpdatesAsync(found);
            Manager.ApplyUpdatesAndRestart(found.TargetFullRelease);
            return true;
        }
        catch { return false; }
    }
}
