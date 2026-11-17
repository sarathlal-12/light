using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BitmapPixelFormat = Windows.Graphics.Imaging.BitmapPixelFormat;
using BitmapAlphaMode = Windows.Graphics.Imaging.BitmapAlphaMode;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Drawing = System.Drawing;
using WF = System.Windows.Forms;

namespace DotLight;

/// <summary>
/// "Grab text": freezes the main screen, lets you drag a box over any part of it, and reads the
/// text inside with the text recognition built into Windows (offline, nothing is uploaded).
/// </summary>
static class GrabText
{
    static Window? overlay;

    /// <summary>Returns the text found, "" if none, or null if cancelled.</summary>
    public static async Task<string?> Run()
    {
        var crop = await Select("Drag over the text to copy it  ·  Esc to cancel");
        if (crop == null) return null;
        try { return await Read(crop); }
        catch { return ""; }
    }

    /// <summary>"Grab image": returns the part of the screen you dragged over, or null if cancelled.</summary>
    public static Task<BitmapSource?> RunImage() => Select("Drag over the part to copy as a picture  ·  Esc to cancel");

    /// <summary>Freezes the screen and lets you drag a box; returns that part of the screen, or null if cancelled.</summary>
    static Task<BitmapSource?> Select(string hintText)
    {
        var done = new TaskCompletionSource<BitmapSource?>();
        if (overlay != null) { overlay.Activate(); done.SetResult(null); return done.Task; }

        var screen = WF.Screen.PrimaryScreen!.Bounds;
        using var shot = new Drawing.Bitmap(screen.Width, screen.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(shot))
            g.CopyFromScreen(screen.Left, screen.Top, 0, 0, shot.Size);
        var frozen = ToBitmapSource(shot);

        var dim = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0));
        var root = new Grid { Cursor = Cursors.Cross };
        root.Children.Add(new Image { Source = frozen, Stretch = Stretch.Fill });
        var shade = new System.Windows.Shapes.Path { Fill = dim };
        root.Children.Add(shade);
        var box = new Rectangle
        {
            StrokeThickness = 1.5, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            RadiusX = 3, RadiusY = 3,
        };
        box.SetResourceReference(Shape.StrokeProperty, "Green");
        root.Children.Add(box);
        var hint = new Border
        {
            Background = Brushes.Black, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 6, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 0, 0),
            Child = new TextBlock
            {
                Text = hintText,
                Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            },
        };
        root.Children.Add(hint);

        var win = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            Background = Brushes.Black, Content = root, Title = ".light grab text",
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        overlay = win;
        // Cover the main screen exactly (in physical pixels, whatever the scaling).
        win.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(win).Handle;
            SetWindowPos(h, new IntPtr(-1), screen.Left, screen.Top, screen.Width, screen.Height, 0x0040);
        };

        Point? start = null;
        void Shade(Rect r)
        {
            var all = new RectangleGeometry(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            shade.Data = r.IsEmpty ? all : new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(r, 3, 3));
        }
        root.Loaded += (_, _) => Shade(Rect.Empty);

        Rect Current(Point p) => new(start!.Value, p);

        root.MouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(root);
            root.CaptureMouse();
            hint.Visibility = Visibility.Collapsed;
        };
        root.MouseMove += (_, e) =>
        {
            if (start == null) return;
            var r = Current(e.GetPosition(root));
            box.Visibility = Visibility.Visible;
            box.Margin = new Thickness(r.X, r.Y, 0, 0);
            box.Width = r.Width;
            box.Height = r.Height;
            Shade(r);
        };
        root.MouseLeftButtonUp += (_, e) =>
        {
            if (start == null) return;
            root.ReleaseMouseCapture();
            var r = Current(e.GetPosition(root));
            start = null;
            if (r.Width < 6 || r.Height < 6) { box.Visibility = Visibility.Collapsed; Shade(Rect.Empty); hint.Visibility = Visibility.Visible; return; }

            // Selection is in screen-independent units; the screenshot is in real pixels.
            double sx = frozen.PixelWidth / root.ActualWidth, sy = frozen.PixelHeight / root.ActualHeight;
            var px = new Int32Rect(
                (int)Math.Max(0, r.X * sx), (int)Math.Max(0, r.Y * sy),
                (int)Math.Min(frozen.PixelWidth - r.X * sx, r.Width * sx),
                (int)Math.Min(frozen.PixelHeight - r.Y * sy, r.Height * sy));
            var crop = new CroppedBitmap(frozen, px);
            crop.Freeze();
            done.TrySetResult(crop);
            win.Close();
        };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.Closed += (_, _) => { overlay = null; done.TrySetResult(null); };
        win.Show();
        win.Activate();
        return done.Task;
    }

    /// <summary>True if Windows has text recognition for one of your languages.</summary>
    public static bool Available => OcrEngine.TryCreateFromUserProfileLanguages() != null;

    /// <summary>Testing: read the text in an image file.</summary>
    internal static async Task<string> ReadFile(string path)
    {
        var img = new BitmapImage();
        img.BeginInit(); img.UriSource = new Uri(path); img.CacheOption = BitmapCacheOption.OnLoad; img.EndInit();
        return await Read(img);
    }

    static async Task<string> Read(BitmapSource image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? throw new InvalidOperationException("No text recognition language installed");

        // Small text reads much better when enlarged first.
        double scale = image.PixelHeight < 120 ? 3 : image.PixelHeight < 300 ? 2 : 1;
        if (scale > 1) image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        double fit = Math.Min(1, OcrEngine.MaxImageDimension / (double)Math.Max(image.PixelWidth, image.PixelHeight));
        if (fit < 1) image = new TransformedBitmap(image, new ScaleTransform(fit, fit));

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ras = new InMemoryRandomAccessStream();
        var s = ras.AsStreamForWrite(); // not disposed: that would close the memory stream too
        
        {
            enc.Save(s);
            await s.FlushAsync();
        }
        ras.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        using var bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bmp);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
    }

    static BitmapSource ToBitmapSource(Drawing.Bitmap bmp)
    {
        var data = bmp.LockBits(new Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally { bmp.UnlockBits(data); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
