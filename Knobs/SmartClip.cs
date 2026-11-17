using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace DotLight.Knobs;

public enum SmartKind { Link, Colour, Path, Email, Phone, Json }

/// <summary>Something you can do with a clip: copy a converted version, open it, or show it in Explorer.</summary>
public sealed record SmartAction(string Label, string? CopyText = null, string? Open = null, string? Reveal = null);

/// <summary>What a copied piece of text turned out to be, plus the actions that make sense for it.</summary>
public sealed record SmartInfo(SmartKind Kind, string Label, string Glyph, IReadOnlyList<SmartAction> Actions, Color? Swatch = null)
{
    public SmartAction Primary => Actions[0];
}

/// <summary>
/// Recognises links, colours, file paths, emails, phone numbers and JSON in copied text, entirely on
/// this PC. It never changes what you copied; actions only run when you click them.
/// </summary>
public static class SmartClip
{
    static readonly Regex Hex = new(@"^#?([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase);
    static readonly Regex Rgb = new(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(,\s*[\d.]+\s*)?\)$", RegexOptions.IgnoreCase);
    static readonly Regex Hsl = new(@"^hsla?\(\s*(\d{1,3}(?:\.\d+)?)\s*,\s*(\d{1,3}(?:\.\d+)?)%\s*,\s*(\d{1,3}(?:\.\d+)?)%\s*(,\s*[\d.]+\s*)?\)$", RegexOptions.IgnoreCase);
    static readonly Regex Mail = new(@"^[^@\s]+@[^@\s]+\.[a-z]{2,}$", RegexOptions.IgnoreCase);
    static readonly Regex PhoneLike = new(@"^\+?[\d\s().-]{7,20}$");
    static readonly Regex DateLike = new(@"^(\d{4}[-./]\d{1,2}[-./]\d{1,2}|\d{1,2}[-./]\d{1,2}[-./]\d{2,4})$");

    // Query parameters that only track you; removed by "Clean link".
    static readonly string[] Trackers = { "fbclid", "gclid", "dclid", "msclkid", "igshid", "mc_cid", "mc_eid", "ref_src", "_hsenc", "_hsmi", "yclid" };

    public static SmartInfo? Detect(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 200_000) return null;

        if (t.Length <= 4096 && !t.Contains('\n'))
        {
            if (AsLink(t) is { } link) return link;
            if (AsColour(t) is { } colour) return colour;
            if (AsPath(t) is { } path) return path;
            if (Mail.IsMatch(t))
                return new(SmartKind.Email, t, "\uE715", new[] { new SmartAction("New mail", Open: "mailto:" + t) });
            // Phone numbers: written with +, spaces, brackets or dashes (plain digit runs are usually IDs or
            // amounts), and never a date like 2026-11-17 or 17/11/2026.
            if (PhoneLike.IsMatch(t) && t.Count(char.IsDigit) >= 7 && !DateLike.IsMatch(t) &&
                (t.StartsWith('+') || t.IndexOfAny(new[] { ' ', '(', '-', '.' }) >= 0))
            {
                var digits = (t.StartsWith('+') ? "+" : "") + new string(t.Where(char.IsDigit).ToArray());
                if (digits != t) return new(SmartKind.Phone, t, "\uE717", new[] { new SmartAction("Digits only", CopyText: digits) });
            }
        }
        return AsJson(t);
    }

    // ---------- links ----------

    static SmartInfo? AsLink(string t)
    {
        if (t.Contains(' ') || !Uri.TryCreate(t, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        var actions = new List<SmartAction>();
        var clean = CleanLink(uri);
        if (clean != t) actions.Add(new SmartAction("Clean link", CopyText: clean));
        actions.Add(new SmartAction("Open", Open: uri.AbsoluteUri));
        var host = uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
        return new(SmartKind.Link, "Link · " + host, "\uE71B", actions);
    }

    /// <summary>Drops tracking parameters (utm_*, fbclid, …; "si" on YouTube/Spotify share links).</summary>
    public static string CleanLink(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query)) return uri.OriginalString;
        bool shareSite = uri.Host.Contains("youtu") || uri.Host.Contains("spotify");
        var kept = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p =>
        {
            var key = p.Split('=')[0].ToLowerInvariant();
            return !key.StartsWith("utm_") && !Trackers.Contains(key) && !(shareSite && (key == "si" || key == "feature"));
        }).ToList();
        var b = new UriBuilder(uri) { Query = string.Join("&", kept) };
        var result = b.Uri.AbsoluteUri;
        // UriBuilder adds default ports and slashes sometimes; keep the original if nothing was removed.
        return kept.Count == uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Length
            ? uri.OriginalString : result;
    }

    // ---------- colours ----------

    static SmartInfo? AsColour(string t)
    {
        Color c;
        string from;
        if (Hex.Match(t) is { Success: true } h)
        {
            var s = h.Groups[1].Value;
            if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
            if (!t.StartsWith('#') && !s.Any(char.IsLetter)) return null; // plain numbers like 123456 aren't colours
            c = Color.FromRgb(Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16));
            from = "hex";
        }
        else if (Rgb.Match(t) is { Success: true } r)
        {
            int R = int.Parse(r.Groups[1].Value), G = int.Parse(r.Groups[2].Value), B = int.Parse(r.Groups[3].Value);
            if (R > 255 || G > 255 || B > 255) return null;
            c = Color.FromRgb((byte)R, (byte)G, (byte)B);
            from = "rgb";
        }
        else if (Hsl.Match(t) is { Success: true } l)
        {
            c = FromHsl(double.Parse(l.Groups[1].Value), double.Parse(l.Groups[2].Value) / 100, double.Parse(l.Groups[3].Value) / 100);
            from = "hsl";
        }
        else return null;

        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        string rgb = $"rgb({c.R}, {c.G}, {c.B})";
        string hsl = ToHsl(c);
        // Offer the other formats first: the one you copied is already on your clipboard.
        var actions = new List<SmartAction>();
        if (from != "rgb") actions.Add(new SmartAction("RGB", CopyText: rgb));
        if (from != "hex") actions.Add(new SmartAction("HEX", CopyText: hex));
        if (from != "hsl") actions.Add(new SmartAction("HSL", CopyText: hsl));
        return new(SmartKind.Colour, hex, "\uE790", actions, c);
    }

    static string ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return $"hsl({Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%)";
    }

    static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    // ---------- file paths ----------

    static SmartInfo? AsPath(string t)
    {
        var p = t.Trim('"');
        // Only local drive paths (C:\...): checking network paths could freeze while Windows looks for the server.
        if (p.Length < 3 || !char.IsLetter(p[0]) || p[1] != ':' || (p[2] != '\\' && p[2] != '/')) return null;
        try
        {
            if (File.Exists(p))
                return new(SmartKind.Path, Path.GetFileName(p), "\uE8A5", new[] { new SmartAction("Show in folder", Reveal: p) });
            if (Directory.Exists(p))
                return new(SmartKind.Path, Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : p, "\uE8B7",
                    new[] { new SmartAction("Open folder", Open: p) });
        }
        catch { }
        return null;
    }

    // ---------- JSON ----------

    static SmartInfo? AsJson(string t)
    {
        if (!(t.StartsWith('{') && t.EndsWith('}')) && !(t.StartsWith('[') && t.EndsWith(']'))) return null;
        try
        {
            using var doc = JsonDocument.Parse(t);
            var tidy = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            var mini = JsonSerializer.Serialize(doc.RootElement);
            var actions = new List<SmartAction>();
            static string Lf(string s) => s.Replace("\r\n", "\n");
            if (Lf(tidy) != Lf(t)) actions.Add(new SmartAction("Tidy", CopyText: tidy));
            if (mini != t) actions.Add(new SmartAction("Minify", CopyText: mini));
            if (actions.Count == 0) return null;
            string label = doc.RootElement.ValueKind == JsonValueKind.Array
                ? $"JSON · {doc.RootElement.GetArrayLength()} items"
                : $"JSON · {doc.RootElement.EnumerateObject().Count()} keys";
            return new(SmartKind.Json, label, "\uE943", actions);
        }
        catch { return null; }
    }
}
