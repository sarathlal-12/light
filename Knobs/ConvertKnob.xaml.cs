using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DotLight.Knobs;

public class ConvItem : INotifyPropertyChanged
{
    public ConvItem(string path) { Path = path; }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string? OutPath { get; set; }
    public bool Done { get; set; }

    string status = "Ready";
    public string Status
    {
        get => status;
        set { status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Converter knob: image format conversion using the codecs built into Windows (WIC).</summary>
public partial class ConvertKnob : UserControl
{
    static readonly HashSet<string> Inputs = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif" };

    public static bool IsImage(string path) => Inputs.Contains(Path.GetExtension(path));

    readonly ObservableCollection<ConvItem> files = new();
    string format = "png";

    public ConvertKnob()
    {
        InitializeComponent();
        List.ItemsSource = files;
        StripSwitch.IsChecked = Settings.Current.StripPhotoInfo;
    }

    void Strip_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.StripPhotoInfo = StripSwitch.IsChecked == true;
        Settings.Current.Save();
        foreach (var f in files.Where(f => f.Done)) { f.Done = false; f.Status = "Ready"; }
        UpdateState();
    }

    /// <summary>Raised while converting with (done, total), and once more when finished.</summary>
    public event Action<int, int>? Progress;

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths.Where(IsImage))
            if (files.All(f => !string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase)))
                files.Add(new ConvItem(p));
        UpdateState();
    }

    void UpdateState()
    {
        EmptyHint.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConvertBtn.IsEnabled = files.Any(f => !f.Done);
        ConvertBtn.Content = format == "pdf" ? "Make PDF" : "Convert";
    }

    // ---------- UI ----------

    void Format_Checked(object sender, RoutedEventArgs e)
    {
        format = (string)((FrameworkElement)sender).Tag;
        if (QualityRow == null) return; // still loading
        QualityRow.Visibility = format is "jpg" or "same" or "pdf" ? Visibility.Visible : Visibility.Collapsed;
        // Changing format makes already converted files convertible again.
        foreach (var f in files.Where(f => f.Done)) { f.Done = false; f.Status = "Ready"; }
        UpdateState();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose images to convert",
            Multiselect = true,
            Filter = "Images|" + string.Join(";", Inputs.Select(x => "*" + x)) + "|All files|*.*",
        };
        App.DialogOpen = true;
        try
        {
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddFiles(dlg.FileNames);
        }
        finally { App.DialogOpen = false; }
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        files.Clear();
        UpdateState();
    }

    void Item_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ConvItem { OutPath: { } outPath } && File.Exists(outPath))
            Process.Start("explorer.exe", $"/select,\"{outPath}\"");
    }

    async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var fmt = format;
        var quality = (int)Quality.Value;
        var maxWidth = int.TryParse(MaxWidthBox.Text.Trim(), out var w) && w > 0 ? w : 0;
        bool strip = Settings.Current.StripPhotoInfo;

        ConvertBtn.IsEnabled = false;
        var todo = files.Where(f => !f.Done).ToList();
        if (fmt == "pdf")
        {
            foreach (var f in todo) f.Status = "Adding…";
            Progress?.Invoke(0, 1);
            try
            {
                var paths = todo.Select(f => f.Path).ToList();
                var pdf = await RunSta(() => MakePdf(paths, quality, maxWidth));
                foreach (var f in todo) { f.OutPath = pdf; f.Done = true; f.Status = "✓ in " + Path.GetFileName(pdf); }
            }
            catch (Exception ex)
            {
                foreach (var f in todo) f.Status = Friendly(ex);
            }
            Progress?.Invoke(1, 1);
            UpdateState();
            return;
        }

        int done = 0;
        Progress?.Invoke(0, todo.Count);
        foreach (var f in todo)
        {
            f.Status = "Converting…";
            try
            {
                f.OutPath = await RunSta(() => ConvertOne(f.Path, fmt, quality, maxWidth, strip));
                f.Done = true;
                f.Status = "✓ " + Path.GetFileName(f.OutPath);
            }
            catch (Exception ex)
            {
                f.Status = Friendly(ex);
            }
            Progress?.Invoke(++done, todo.Count);
        }
        UpdateState();
    }

    /// <summary>Testing: convert files straight away and report where they went.</summary>
    internal static async Task<string> TestConvert(string[] paths, string fmt, int quality, int maxWidth, bool strip)
    {
        if (fmt == "pdf") return await RunSta(() => MakePdf(paths.ToList(), quality, maxWidth));
        var outs = new List<string>();
        foreach (var p in paths) outs.Add(await RunSta(() => ConvertOne(p, fmt, quality, maxWidth, strip)));
        return string.Join(" | ", outs);
    }

    static string Friendly(Exception ex) => ex switch
    {
        NotSupportedException or FileFormatException => "Not an image Windows can open",
        UnauthorizedAccessException => "No permission to save in that folder",
        IOException => "File is in use or the disk is full",
        _ => "Failed: " + ex.Message,
    };
    // ---------- conversion ----------

    /// <summary>WIC objects are happiest on an STA thread; run each conversion on its own one.</summary>
    static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    /// <summary>Loads the first frame, turned upright, plus its photo details (if any).</summary>
    static (BitmapSource Image, BitmapMetadata? Info, bool Rotated) Load(string path)
    {
        BitmapSource src;
        BitmapMetadata? info = null;
        ushort orientation = 1;
        using (var fs = File.OpenRead(path))
        {
            var dec = BitmapDecoder.Create(fs,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            var frame = dec.Frames[0];
            src = frame;
            try
            {
                if (frame.Metadata is BitmapMetadata md)
                {
                    info = md.Clone();
                    if (md.ContainsQuery("System.Photo.Orientation") && md.GetQuery("System.Photo.Orientation") is ushort o)
                        orientation = o;
                }
            }
            catch { /* no usable metadata */ }
        }

        // Phone photos are often stored sideways with a "rotate me" flag.
        double angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        if (angle != 0) src = new TransformedBitmap(src, new RotateTransform(angle));
        // A plain copy of the pixels: carries no hidden details along unless we add them back.
        src = new FormatConvertedBitmap(src, src.Format, src.Palette, 0);
        return (src, info, angle != 0);
    }

    /// <summary>The output format for "Same": the input's own, or JPG for formats Windows can read but not write.</summary>
    static string SameFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "png", ".jpg" or ".jpeg" or ".jfif" => "jpg", ".gif" => "gif", ".bmp" => "bmp",
        ".tif" or ".tiff" => "tif", ".ico" => "ico", _ => "jpg",
    };

    static string ConvertOne(string path, string fmt, int quality, int maxWidth, bool strip)
    {
        if (fmt == "same") fmt = SameFormat(path);
        var (src, info, rotated) = Load(path);

        if (maxWidth > 0 && src.PixelWidth > maxWidth)
            src = Scale(src, (double)maxWidth / src.PixelWidth);

        if (fmt == "ico")
        {
            int largest = Math.Max(src.PixelWidth, src.PixelHeight);
            if (largest > 256) src = Scale(src, 256.0 / largest);
        }

        if (fmt is "jpg" or "bmp") src = FlattenOnWhite(src); // these formats have no transparency

        var outPath = FreePath(path, fmt);
        if (fmt == "ico")
        {
            WriteIco(src, outPath);
            return outPath;
        }

        BitmapEncoder enc = fmt switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = quality },
            "bmp" => new BmpBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            "tif" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        var keep = strip ? null : KeepableInfo(info, path, fmt, rotated);
        try
        {
            enc.Frames.Add(BitmapFrame.Create(src, null, keep, null));
            using var output = File.Create(outPath);
            enc.Save(output);
        }
        catch when (keep != null)
        {
            // The details didn't fit the new file: save the picture without them.
            enc = (BitmapEncoder)Activator.CreateInstance(enc.GetType())!;
            if (enc is JpegBitmapEncoder j) j.QualityLevel = quality;
            enc.Frames.Add(BitmapFrame.Create(src));
            using var output = File.Create(outPath);
            enc.Save(output);
        }
        return outPath;
    }

    /// <summary>Photo details can only be carried over between the same kind of file (JPG→JPG, TIFF→TIFF).</summary>
    static BitmapMetadata? KeepableInfo(BitmapMetadata? info, string path, string fmt, bool rotated)
    {
        if (info == null) return null;
        bool sameKind = fmt == "jpg" && info.Format == "jpg" || fmt == "tif" && info.Format == "tiff";
        if (!sameKind) return null;
        try
        {
            if (rotated) info.SetQuery("System.Photo.Orientation", (ushort)1); // the pixels are upright now
            return info;
        }
        catch { return null; }
    }

    // ---------- PDF ----------

    /// <summary>Every image becomes one page of a single PDF, saved next to the first image.</summary>
    static string MakePdf(List<string> paths, int quality, int maxWidth)
    {
        var pages = new List<(byte[] Jpeg, int W, int H)>();
        foreach (var p in paths)
        {
            var (src, _, _) = Load(p);
            if (maxWidth > 0 && src.PixelWidth > maxWidth) src = Scale(src, (double)maxWidth / src.PixelWidth);
            src = FlattenOnWhite(src);
            var enc = new JpegBitmapEncoder { QualityLevel = quality };
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            pages.Add((ms.ToArray(), src.PixelWidth, src.PixelHeight));
        }
        var outPath = FreePath(paths[0], "pdf");
        Pdf.Write(outPath, pages);
        return outPath;
    }
    static BitmapSource Scale(BitmapSource src, double s) =>
        new TransformedBitmap(src, new ScaleTransform(s, s));

    static BitmapSource FlattenOnWhite(BitmapSource src)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);

        var rgb = new byte[w * h * 3];
        for (int i = 0, j = 0; i < px.Length; i += 4, j += 3)
        {
            int a = px[i + 3];
            for (int c = 0; c < 3; c++)
                rgb[j + c] = (byte)((px[i + c] * a + 255 * (255 - a)) / 255);
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, rgb, w * 3);
    }

    /// <summary>An .ico file holding a single PNG image (supported since Windows Vista).</summary>
    static void WriteIco(BitmapSource src, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var png = ms.ToArray();

        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0);            // reserved
        w.Write((short)1);            // type: icon
        w.Write((short)1);            // image count
        w.Write((byte)(src.PixelWidth >= 256 ? 0 : src.PixelWidth));
        w.Write((byte)(src.PixelHeight >= 256 ? 0 : src.PixelHeight));
        w.Write((byte)0);             // palette size
        w.Write((byte)0);             // reserved
        w.Write((short)1);            // colour planes
        w.Write((short)32);           // bits per pixel
        w.Write(png.Length);          // image size
        w.Write(22);                  // image offset (6 + 16)
        w.Write(png);
    }

    /// <summary>photo.png â†’ photo.jpg, or photo (1).jpg if that already exists.</summary>
    static string FreePath(string source, string fmt)
    {
        var dir = Path.GetDirectoryName(source)!;
        var name = Path.GetFileNameWithoutExtension(source);
        var ext = "." + fmt;
        var candidate = Path.Combine(dir, name + ext);
        for (int i = 1; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} ({i}){ext}");
        return candidate;
    }
}
