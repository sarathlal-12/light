using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DotLight;

/// <summary>
/// The colour of .light's light: waveform, dial needle, progress bars, selected tab, highlights.
/// "iOS" keeps a colour per knob (green music, blue clipboard, orange convert); every other
/// choice uses one colour everywhere. Colour changes fade rather than snap.
/// </summary>
static class Theme
{
    public static readonly (string Key, string Label)[] Choices =
    {
        ("iOS", "iOS"), ("White", "White"), ("Amber", "Amber"), ("Ice", "Ice"), ("Mint", "Mint"),
        ("Magenta", "Magenta"), ("Violet", "Violet"), ("Coral", "Coral"),
        ("Album", "Album art"), ("Windows", "Windows accent"),
    };

    static readonly Color IosGreen = C("#30D158"), IosBlue = C("#0A84FF"), IosOrange = C("#FF9F0A");

    static readonly Dictionary<string, Color> Presets = new()
    {
        ["White"] = C("#F2F2F7"),
        ["Amber"] = C("#FFB340"),
        ["Ice"] = C("#7FD4FF"),
        ["Mint"] = C("#5FE3C0"),
        ["Magenta"] = C("#FF4FD8"),
        ["Violet"] = C("#A78BFA"),
        ["Coral"] = C("#FF7A6B"),
    };

    // App-wide colour resources get locked by WPF, so instead of animating a brush we tween the
    // colours ourselves and swap in a fresh brush each frame; everything uses DynamicResource.
    static Application app = null!;
    static Color[] now = { IosGreen, IosBlue, IosOrange }, from = new Color[3], to = new Color[3];
    static readonly string[] Keys = { "Green", "Blue", "Orange" };
    static TimeSpan fadeStart;
    static bool fading;
    const double FadeMs = 450;
    static ImageSource? cover;

    /// <summary>Puts the live brushes in place of the app's colour resources. Call before any window is created.</summary>
    public static void Init(Application application)
    {
        app = application;
        Apply(animate: false);

        // Follow changes to the Windows accent colour.
        SystemEvents.UserPreferenceChanged += (_, _) =>
            app.Dispatcher.BeginInvoke(() => { if (Settings.Current.Light == "Windows") Apply(); });
    }

    /// <summary>The playing song's cover changed (used by "Album art").</summary>
    public static void SetCover(ImageSource? art)
    {
        if (ReferenceEquals(cover, art)) return;
        cover = art;
        if (Settings.Current.Light == "Album") Apply();
    }

    public static void Apply(bool animate = true)
    {
        var (m, c, v) = Resolve(Settings.Current.Light);
        to = new[] { m, c, v };
        if (!animate)
        {
            now = (Color[])to.Clone();
            Publish();
            return;
        }
        from = (Color[])now.Clone();
        fadeStart = TimeSpan.Zero;
        if (!fading) { fading = true; CompositionTarget.Rendering += OnFrame; }
    }

    static void OnFrame(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (fadeStart == TimeSpan.Zero) fadeStart = t;
        double p = Math.Min(1, (t - fadeStart).TotalMilliseconds / FadeMs);
        double eased = p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2; // ease in-out
        for (int i = 0; i < 3; i++) now[i] = Mix(from[i], to[i], eased);
        Publish();
        if (p >= 1) { fading = false; CompositionTarget.Rendering -= OnFrame; }
    }

    static void Publish()
    {
        for (int i = 0; i < 3; i++)
        {
            var brush = new SolidColorBrush(now[i]);
            brush.Freeze();
            app.Resources[Keys[i]] = brush;
        }
        Changed?.Invoke();
    }

    /// <summary>Raised every time the live colours change (each frame of a fade too).</summary>
    public static event Action? Changed;

    /// <summary>The live colour behind "Green", "Blue" or "Orange" right now.</summary>
    public static Color Current(string key) => now[Math.Max(0, Array.IndexOf(Keys, key))];

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static (Color, Color, Color) Resolve(string key)
    {
        Color? one = key switch
        {
            "Album" => cover is BitmapSource b ? FromArt(b) : null,
            "Windows" => WindowsAccent(),
            _ => Presets.TryGetValue(key, out var p) ? p : null,
        };
        return one is { } x ? (x, x, x) : (IosGreen, IosBlue, IosOrange); // iOS, or album art with nothing playing
    }

    /// <summary>A brush for the picker's little swatch.</summary>
    public static Brush Swatch(string key)
    {
        Brush b = key switch
        {
            "iOS" => Gradient(IosGreen, IosBlue, IosOrange),
            "Album" => Gradient(C("#FF5E9C"), C("#FFD166"), C("#4CC9F0")),
            "Windows" => new SolidColorBrush(WindowsAccent()),
            _ => new SolidColorBrush(Presets[key]),
        };
        b.Freeze();
        return b;
    }

    // ---------- colour sources ----------

    /// <summary>The Windows accent colour, brightened if needed so it glows on black.</summary>
    static Color WindowsAccent()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int abgr)
                return Glowing(Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16)));
        }
        catch { }
        return IosBlue;
    }

    /// <summary>
    /// The most vivid colour in a cover: shrinks it to 16×16 and averages the pixels weighted by how
    /// colourful they are, so a grey background doesn't wash out a red logo.
    /// </summary>
    static Color? FromArt(BitmapSource art)
    {
        try
        {
            var small = new FormatConvertedBitmap(
                new TransformedBitmap(art, new ScaleTransform(16.0 / art.PixelWidth, 16.0 / art.PixelHeight)),
                PixelFormats.Bgra32, null, 0);
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double pb = px[i] / 255.0, pg = px[i + 1] / 255.0, pr = px[i + 2] / 255.0;
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double sat = max == 0 ? 0 : (max - min) / max;
                double weight = sat * sat * max + 0.001;
                r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
            }
            if (total < 0.05) return Presets["White"]; // black-and-white cover
            return Glowing(Color.FromRgb((byte)(r / total * 255), (byte)(g / total * 255), (byte)(b / total * 255)));
        }
        catch { return null; }
    }

    /// <summary>Keeps the hue but makes sure the colour is bright and saturated enough to read on black.</summary>
    static Color Glowing(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double hue = 0, d = max - min;
        if (d > 0)
        {
            if (max == r) hue = 60 * (((g - b) / d) % 6);
            else if (max == g) hue = 60 * ((b - r) / d + 2);
            else hue = 60 * ((r - g) / d + 4);
        }
        if (hue < 0) hue += 360;
        double sat = max == 0 ? 0 : d / max;
        if (sat < 0.08) return Presets["White"];
        return FromHsv(hue, Math.Max(sat, 0.55), Math.Max(max, 0.9));
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    static LinearGradientBrush Gradient(Color a, Color b, Color c) => new(new GradientStopCollection
    {
        new GradientStop(a, 0), new GradientStop(b, 0.5), new GradientStop(c, 1),
    }, new Point(0, 0), new Point(1, 1));

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
