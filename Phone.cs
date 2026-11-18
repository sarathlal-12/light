using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DotLight;

/// <summary>A notification your phone sent to Windows through Phone Link.</summary>
public record PhoneNote(string App, string Title, string Body, bool IsCall, string? Code, DateTime Time);

/// <summary>
/// Phone notifications, read from Windows' own notification store (read-only). Phone Link puts your
/// Android or iPhone notifications there; .light only picks out the ones from Phone Link.
/// Uses the SQLite that ships with Windows (winsqlite3), and opens the file only for a moment each time.
/// </summary>
static class PhoneNotes
{
    static string Db => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Notifications\wpndatabase.db");
    static long lastOrder = -1;

    /// <summary>New phone notifications since the last call. The first call only remembers where we are.</summary>
    public static List<PhoneNote> TakeNew()
    {
        var found = new List<PhoneNote>();
        if (!File.Exists(Db)) return found;
        IntPtr db = IntPtr.Zero, st = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(Utf8(Db), out db, 1 /* read only */, IntPtr.Zero) != 0) return found;
            if (lastOrder < 0)
            {
                lastOrder = 0;
                if (sqlite3_prepare_v2(db, Utf8("select max([Order]) from Notification"), -1, out st, IntPtr.Zero) == 0 && sqlite3_step(st) == 100)
                    lastOrder = sqlite3_column_int64(st, 0);
                return found;
            }
            // Phone Link registers each phone app with Windows (…!YourPhoneNotifications_net.whatsapp.WhatsApp)
            // and keeps its proper name ("WhatsApp"); calls come from …!YourPhoneCalling.
            const string sql = "select n.[Order], n.Payload, h.PrimaryId, " +
                               "(select a.AssetValue from HandlerAssets a where a.HandlerId = h.RecordId and a.AssetKey = 'DisplayName') " +
                               "from Notification n join NotificationHandler h on h.RecordId = n.HandlerId " +
                               "where n.[Order] > ?1 and n.Type = 'toast' and h.PrimaryId like 'Microsoft.YourPhone%' order by n.[Order]";
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out st, IntPtr.Zero) != 0) return found;
            sqlite3_bind_int64(st, 1, lastOrder);
            while (sqlite3_step(st) == 100)
            {
                lastOrder = Math.Max(lastOrder, sqlite3_column_int64(st, 0));
                int n = sqlite3_column_bytes(st, 1);
                if (n <= 0 || n > 256_000) continue;
                var bytes = new byte[n];
                Marshal.Copy(sqlite3_column_blob(st, 1), bytes, 0, n);
                string handler = Text(st, 2) ?? "";
                string? appName = Text(st, 3);
                if (Parse(Encoding.UTF8.GetString(bytes), AppFrom(handler, appName), handler.EndsWith("!YourPhoneCalling", StringComparison.OrdinalIgnoreCase)) is { } note)
                    found.Add(note);
            }
        }
        catch { }
        finally
        {
            if (st != IntPtr.Zero) sqlite3_finalize(st);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
        }
        return found;
    }

    static string? Text(IntPtr st, int col)
    {
        var p = sqlite3_column_text(st, col);
        return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
    }

    /// <summary>The phone app's name: what Phone Link stored ("WhatsApp"), else the end of its id ("…net.whatsapp.WhatsApp").</summary>
    internal static string? AppFrom(string handler, string? displayName)
    {
        var name = displayName?.Trim('‎', '‏', ' ');
        if (!string.IsNullOrEmpty(name) && !name.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)) return name;
        const string tag = "YourPhoneNotifications_";
        int i = handler.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var id = handler[(i + tag.Length)..];
        var last = id[(id.LastIndexOf('.') + 1)..];
        return last.Length == 0 ? null : char.ToUpperInvariant(last[0]) + last[1..];
    }

    static readonly Regex Placeholder = new(@"^\d+ new notifications?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads a toast: the app (from Phone Link, the attribution or the header), the sender, and the message.</summary>
    internal static PhoneNote? Parse(string xml, string? appHint = null, bool isCall = false)
    {
        try
        {
            var x = XElement.Parse(xml.TrimEnd('\0'));
            if (x.Name.LocalName != "toast") return null;
            var texts = x.Descendants().Where(e => e.Name.LocalName == "text").ToList();
            string? attribution = texts.FirstOrDefault(t => (string?)t.Attribute("placement") == "attribution")?.Value;
            var lines = texts.Where(t => (string?)t.Attribute("placement") != "attribution")
                             .Select(t => t.Value.Trim()).Where(s => s.Length > 0).ToList();
            if (lines.Count == 0) return null;
            string? header = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "header")?.Attribute("title")?.Value;
            string app = (appHint ?? attribution ?? header ?? "Phone").Trim();
            string title = lines[0], body = string.Join(" ", lines.Skip(1));
            // The phone hid the content (iPhone "Show Previews" isn't Always): all we get is "1 new notification".
            if (lines.Count == 1 && Placeholder.IsMatch(title)) { body = title; title = app; app = "Phone"; }
            bool call = isCall || (string?)x.Attribute("scenario") == "incomingCall" ||
                        ((string?)x.Attribute("launch") ?? "").Contains("call", StringComparison.OrdinalIgnoreCase) && lines.Any(l => l.Contains("call", StringComparison.OrdinalIgnoreCase));
            return new PhoneNote(app, title, body, call, FindCode(title + " " + body), DateTime.Now);
        }
        catch { return null; }
    }

    static readonly Regex CodeNear = new(@"(?i)(?:code|otp|passcode|verification|verify|one[- ]time|pin|security)\D{0,40}?\b(\d{4,8})\b|\b(\d{4,8})\b\D{0,30}?(?:is your|code|otp)", RegexOptions.Compiled);

    /// <summary>A login / one-time code in the text, if there is one.</summary>
    internal static string? FindCode(string text)
    {
        var m = CodeNear.Match(text);
        return !m.Success ? null : m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int len, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_bind_int64(IntPtr stmt, int i, long v);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern long sqlite3_column_int64(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr stmt);
}

/// <summary>A phone connected to this PC over Bluetooth, and its battery when Windows knows it.</summary>
public record PhoneLink(string Name, int? Battery);

static class PhoneStatus
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public static async Task<PhoneLink?> Find()
    {
        try
        {
            var sel = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var devices = await DeviceInformation.FindAllAsync(sel, new[] { "System.Devices.ContainerId" });
            foreach (var d in devices)
            {
                using var bt = await BluetoothDevice.FromIdAsync(d.Id);
                if (bt == null || bt.ClassOfDevice.MajorClass != BluetoothMajorClass.Phone) continue;
                int? battery = null;
                if (d.Properties.TryGetValue("System.Devices.ContainerId", out var c) && c is Guid container)
                {
                    var parts = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"",
                        new[] { BatteryKey }, DeviceInformationKind.Device);
                    foreach (var p in parts)
                        if (p.Properties.TryGetValue(BatteryKey, out var b) && b is byte level) { battery = level; break; }
                }
                return new PhoneLink(bt.Name, battery);
            }
        }
        catch { }
        return null;
    }
}

/// <summary>
/// iPhone clipboard: a tiny listener an Apple Shortcut can send copied text to. Off unless you turn it on.
/// Only answers devices on your own network (private addresses) that send the pairing code, takes text
/// only (up to 100 KB), and never sends anything back but "ok".
/// </summary>
sealed class PhoneClipListener
{
    public const int Port = 47800;
    TcpListener? listener;
    CancellationTokenSource? stop;

    /// <summary>Text that arrived from the phone (on a background thread).</summary>
    public event Action<string>? Received;

    public bool Running => listener != null;

    public string? Start()
    {
        if (listener != null) return null;
        try
        {
            // test runs listen on this PC only (no firewall prompt)
            var where = Environment.GetEnvironmentVariable("LIGHT_TEST") != null ? IPAddress.Loopback : IPAddress.Any;
            listener = new TcpListener(where, Port);
            listener.Start();
            stop = new CancellationTokenSource();
            _ = Loop(listener, stop.Token);
            return null;
        }
        catch (Exception ex) { listener = null; return "Couldn't start: " + ex.Message; }
    }

    public void Stop()
    {
        stop?.Cancel();
        try { listener?.Stop(); } catch { }
        listener = null;
    }

    async Task Loop(TcpListener l, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(token); } catch { return; }
            _ = Handle(client);
        }
    }

    async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint ep || !IsLocal(ep.Address)) return;
                client.ReceiveTimeout = 4000;
                var s = client.GetStream();
                using var cts = new CancellationTokenSource(5000);
                var head = await ReadHead(s, cts.Token);
                if (head == null) return;
                var lines = head.Header.Split("\r\n");
                var first = lines[0].Split(' ');
                var headers = new Dictionary<string, string>();
                foreach (var h in lines.Skip(1).Select(h => h.Split(':', 2)).Where(p => p.Length == 2))
                    headers[h[0].Trim().ToLowerInvariant()] = h[1].Trim();
                if (first.Length < 2) return;
                string method = first[0];
                var target = first[1].Split('?', 2);
                string path = target[0];
                // the code comes in the X-Light-Code header, or in the link itself (?code=...)
                string? code = headers.TryGetValue("x-light-code", out var hc) ? hc : QueryValue(target.Length > 1 ? target[1] : "", "code");

                if (method == "GET" && path == "/ping") { await Reply(s, 200, ".light"); return; }
                if (path != "/clip") { await Reply(s, 404, "not here"); return; }
                if (code == null || !Same(code, Settings.Current.PhoneClipCode)) { await Reply(s, 403, "wrong code"); return; }
                // opened in Safari (from the QR code): a small page to copy the link and get the Shortcut
                if (method == "GET") { await Reply(s, 200, SetupPage(), "text/html"); return; }
                if (method != "POST") { await Reply(s, 405, "POST only"); return; }
                if (!headers.TryGetValue("content-length", out var lenText) || !int.TryParse(lenText, out int len) || len <= 0 || len > 100_000)
                { await Reply(s, 413, "text only, up to 100 KB"); return; }

                var body = new byte[len];
                int have = Math.Min(head.Extra.Length, len);
                Array.Copy(head.Extra, body, have);
                while (have < len)
                {
                    int r = await s.ReadAsync(body.AsMemory(have, len - have), cts.Token);
                    if (r <= 0) return;
                    have += r;
                }
                var text = Encoding.UTF8.GetString(body).Trim();
                if (text.Length == 0) { await Reply(s, 400, "empty"); return; }
                Received?.Invoke(text);
                await Reply(s, 200, "ok");
            }
            catch { }
        }
    }

    record Head(string Header, byte[] Extra);

    static async Task<Head?> ReadHead(NetworkStream s, CancellationToken token)
    {
        var buf = new byte[8192];
        int n = 0;
        while (n < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(n), token);
            if (r <= 0) return null;
            n += r;
            for (int i = 3; i < n; i++)
                if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n')
                    return new Head(Encoding.ASCII.GetString(buf, 0, i - 3), buf[(i + 1)..n]);
        }
        return null; // headers too big
    }

    static string? QueryValue(string query, string key)
    {
        foreach (var part in query.Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key) return Uri.UnescapeDataString(kv[1].Replace('+', ' '));
        }
        return null;
    }

    /// <summary>The shared "Send to .light" Shortcut (an iCloud link), shown on the setup page. Empty until it's published.</summary>
    public const string ShortcutLink = "https://www.icloud.com/shortcuts/43c3ab80086b4feaae0ea014122bb30d";

    /// <summary>This PC's full link for the Shortcut, code included.</summary>
    public static string Link(string address) => $"{address}?code={Uri.EscapeDataString(Settings.Current.PhoneClipCode ?? "")}";

    /// <summary>What Safari shows after scanning the QR code: copy the link, then add the Shortcut and paste it.</summary>
    static string SetupPage()
    {
        string address = WebUtility.HtmlEncode(Addresses().FirstOrDefault() ?? "");
        string code = WebUtility.HtmlEncode(Settings.Current.PhoneClipCode ?? "");
        string get = $"<a class=b href=\"{WebUtility.HtmlEncode(ShortcutLink)}\">1. Get the Shortcut</a>";
        return $$"""
            <!doctype html><html><head><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
            <title>.light</title><style>
            body{margin:0;padding:28px 20px;background:#0E0E0F;color:#F2EFE8;font:16px -apple-system,system-ui,sans-serif}
            h1{font-size:22px;margin:0 0 6px} p{color:#9A9AA2;line-height:1.45;margin:8px 0 18px}
            textarea{width:100%;box-sizing:border-box;height:72px;border-radius:12px;border:1px solid #333;background:#1A1A1C;color:#F2EFE8;font:14px ui-monospace,monospace;padding:10px}
            .b{display:block;text-align:center;padding:14px;border-radius:999px;background:#B8F23A;color:#0E0E0F;font-weight:600;text-decoration:none;border:0;width:100%;font-size:16px;margin-top:10px}
            </style></head><body>
            <h1>.light on this PC</h1><p>Send what you copy on this iPhone to .light. Phone and PC need the same Wi-Fi.</p>
            {{get}}
            <p>2. When you add it, it asks for these two. Copy each one in turn:</p>
            <textarea id=a readonly>{{address}}</textarea>
            <button class=b onclick="c('a',this)">Copy address</button>
            <textarea id=k readonly style="height:44px">{{code}}</textarea>
            <button class=b onclick="c('k',this)">Copy code</button>
            <script>function c(id,b){var t=document.getElementById(id);t.focus();t.setSelectionRange(0,9999);document.execCommand('copy');b.textContent='Copied'}</script>
            </body></html>
            """;
    }

    static async Task Reply(NetworkStream s, int status, string text, string type = "text/plain")
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head); await s.WriteAsync(body);
    }

    static bool Same(string a, string? b) =>
        b != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>Your own network only: 10.x, 172.16-31.x, 192.168.x, link-local, or this PC.</summary>
    static bool IsLocal(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Addresses the Shortcut can use: this PC's name on the network, and its Wi-Fi/Ethernet address.</summary>
    public static List<string> Addresses()
    {
        // the PC's name works across Wi-Fi changes, but only if it's a valid network name (no "_" etc.)
        string host = Environment.MachineName.ToLowerInvariant();
        bool goodName = host.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');
        var list = new List<string>();
        if (goodName) list.Add($"http://{host}.local:{Port}/clip");
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var ip = nic.GetIPProperties();
                if (ip.GatewayAddresses.Count == 0) continue; // virtual adapters have no way out
                foreach (var u in ip.UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && IsLocal(u.Address))
                        list.Add($"http://{u.Address}:{Port}/clip");
            }
        }
        catch { }
        return list.Distinct().ToList();
    }

    /// <summary>A fresh pairing code like "K7Q2-M9XD" (no look-alike letters).</summary>
    public static string NewCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var b = RandomNumberGenerator.GetBytes(8);
        var s = new string(b.Select(x => chars[x % chars.Length]).ToArray());
        return s[..4] + "-" + s[4..];
    }
}
