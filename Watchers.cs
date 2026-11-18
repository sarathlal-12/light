using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;
using WF = System.Windows.Forms;

namespace DotLight;

/// <summary>
/// Which apps are using the camera or microphone right now. Reads (never changes) the record Windows
/// keeps for its own privacy indicators: an app is "in use" while its stop time is still empty.
/// </summary>
static class Privacy
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    public static (List<string> Camera, List<string> Mic) InUse()
    {
        var cam = new List<string>(); var mic = new List<string>();
        Scan("webcam", cam);
        Scan("microphone", mic);
        return (cam, mic);
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
