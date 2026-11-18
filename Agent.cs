using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace DotLight;

/// <summary>One thing Claude Code told us: it needs your OK, or it has finished.</summary>
record AgentEvent(string Kind, string Session, string Project, string Message, IntPtr Window);

/// <summary>
/// Claude Code status on the island. Claude Code runs "light.exe --agent" as a hook when it needs
/// your permission (Notification) or finishes (Stop); that short-lived process passes the event to
/// the running .light over a local pipe that only your Windows user can open, then exits.
/// </summary>
static class Agent
{
    static readonly string PipeName = "DotLight.Agent." + Environment.UserName;

    // ---------- hook side (runs inside Claude Code's hook, must be quick and never fail) ----------

    public static int ForwardFromHook()
    {
        try
        {
            var read = Task.Run(() => new StreamReader(Console.OpenStandardInput(), Encoding.UTF8).ReadToEnd());
            if (!read.Wait(1500)) { Log("no input"); return 0; }
            Log("input " + read.Result.Length + " chars");
            var json = JsonNode.Parse(read.Result);
            if (json == null) return 0;

            string hookEvent = (string?)json["hook_event_name"] ?? "";
            string message = (string?)json["message"] ?? "";
            string kind = hookEvent switch
            {
                "Stop" => "done",
                "Notification" when message.Contains("permission", StringComparison.OrdinalIgnoreCase)
                                 || (string?)json["notification_type"] == "permission_prompt" => "needs",
                _ => "",
            };
            if (kind == "") return 0;

            string cwd = (string?)json["cwd"] ?? "";
            var payload = new JsonObject
            {
                ["kind"] = kind,
                ["session"] = (string?)json["session_id"] ?? "",
                ["project"] = cwd.Length > 0 ? Path.GetFileName(cwd.TrimEnd('\\', '/')) : "",
                ["message"] = message,
                ["window"] = (long)FindTerminalWindow(),
            };

            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(400); // .light not running: just give up quietly
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString() + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            Log("sent " + kind);
        }
        catch (Exception ex) { Log("failed: " + ex.GetType().Name + " " + ex.Message); }
        return 0; // never get in Claude Code's way
    }

    /// <summary>Testing only: LIGHT_AGENT_LOG names a file that gets one line per step.</summary>
    static void Log(string line)
    {
        var f = Environment.GetEnvironmentVariable("LIGHT_AGENT_LOG");
        if (string.IsNullOrEmpty(f)) return;
        try { File.AppendAllText(f, $"{DateTime.Now:HH:mm:ss.fff} {line}" + Environment.NewLine); } catch { }
    }

    /// <summary>Walks up from this process to the first parent with a visible window: the terminal or editor Claude Code runs in.</summary>
    static IntPtr FindTerminalWindow()
    {
        try
        {
            var parents = ParentMap();
            int pid = Environment.ProcessId;
            for (int i = 0; i < 12 && parents.TryGetValue(pid, out int parent) && parent > 0; i++)
            {
                pid = parent;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                }
                catch { }
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == (IntPtr)(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref e)) return map;
            do map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
            while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    // ---------- .light side ----------

    public static event Action<AgentEvent>? Received;

    /// <summary>For testing: the last event that came in.</summary>
    internal static AgentEvent? LastReceived;

    /// <summary>Listens for hook events for as long as .light runs.</summary>
    public static void Listen(Dispatcher ui)
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var readLine = reader.ReadLineAsync();
                    if (await Task.WhenAny(readLine, Task.Delay(2000)) != readLine) continue;
                    var line = readLine.Result;
                    if (string.IsNullOrWhiteSpace(line) || line.Length > 64_000) continue;
                    var ev = Parse(line);
                    if (ev != null) { LastReceived = ev; _ = ui.BeginInvoke(() => Received?.Invoke(ev)); }
                }
                catch { await Task.Delay(500); }
            }
        });
    }

    internal static AgentEvent? Parse(string line)
    {
        try
        {
            var j = JsonNode.Parse(line);
            if (j == null) return null;
            string kind = (string?)j["kind"] ?? "";
            if (kind is not ("needs" or "done")) return null;
            return new AgentEvent(kind, (string?)j["session"] ?? "", (string?)j["project"] ?? "",
                (string?)j["message"] ?? "", new IntPtr((long?)j["window"] ?? 0));
        }
        catch { return null; }
    }

    // ---------- the hook entry in Claude Code's settings ----------

    /// <summary>Claude Code's user settings file (LIGHT_CLAUDE_SETTINGS overrides it, for testing).</summary>
    static string SettingsPath =>
        Environment.GetEnvironmentVariable("LIGHT_CLAUDE_SETTINGS") is { Length: > 0 } p ? p
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    static readonly string[] Events = { "Notification", "Stop" };

    /// <summary>Forward slashes work both in Git Bash (which Claude Code uses for hooks) and in cmd.</summary>
    static string Command => $"\"{(Environment.ProcessPath ?? "light.exe").Replace('\\', '/')}\" --agent";

    static bool IsOurs(JsonNode? hook) =>
        ((string?)hook?["command"])?.Contains("--agent") == true &&
        ((string?)hook?["command"])!.Contains("light.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds (or refreshes) the hook. Returns null on success, or a short reason it couldn't.</summary>
    public static string? AddHook() => Edit(add: true);

    /// <summary>Removes only .light's hook entries, leaving everything else as it was.</summary>
    public static string? RemoveHook() => Edit(add: false);

    static string? Edit(bool add)
    {
        try
        {
            var path = SettingsPath;
            JsonObject root;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                try { root = JsonNode.Parse(text.Length == 0 ? "{}" : text) as JsonObject ?? throw new JsonException(); }
                catch (JsonException) { return "Couldn't read Claude Code's settings.json, so it wasn't changed."; }
            }
            else
            {
                if (!add) return null;
                root = new JsonObject();
            }

            var hooks = root["hooks"] as JsonObject;
            if (hooks == null)
            {
                if (!add) return null;
                root["hooks"] = hooks = new JsonObject();
            }

            bool changed = false;
            foreach (var ev in Events)
            {
                var list = hooks[ev] as JsonArray;
                // drop our old entries (an old path, or removing)
                if (list != null)
                {
                    foreach (var group in list.OfType<JsonObject>().ToList())
                    {
                        if (group["hooks"] is not JsonArray inner) continue;
                        foreach (var h in inner.Where(IsOurs).ToList()) { inner.Remove(h); changed = true; }
                        if (inner.Count == 0) { list.Remove(group); changed = true; }
                    }
                    if (list.Count == 0 && !add) { hooks.Remove(ev); changed = true; }
                }
                if (add)
                {
                    if (list == null) hooks[ev] = list = new JsonArray();
                    var entry = new JsonObject { ["type"] = "command", ["command"] = Command, ["timeout"] = 5 };
                    list.Add(new JsonObject { ["hooks"] = new JsonArray(entry) });
                    changed = true;
                }
            }
            if (hooks.Count == 0) root.Remove("hooks");
            if (!changed) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Copy(path, path + ".light-backup", overwrite: true);
            var tmp = path + ".light-tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return null;
        }
        catch (Exception ex) { return "Couldn't update Claude Code's settings: " + ex.Message; }
    }

    /// <summary>True if Claude Code's settings currently run this copy of .light.</summary>
    public static bool HookInstalled
    {
        get
        {
            try
            {
                if (!File.Exists(SettingsPath)) return false;
                var hooks = JsonNode.Parse(File.ReadAllText(SettingsPath))?["hooks"] as JsonObject;
                return hooks != null && Events.All(ev => (hooks[ev] as JsonArray)?.OfType<JsonObject>()
                    .Any(g => (g["hooks"] as JsonArray)?.Any(h => (string?)h?["command"] == Command) == true) == true);
            }
            catch { return false; }
        }
    }

    // ---------- native ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
