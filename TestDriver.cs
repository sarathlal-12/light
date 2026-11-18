using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DotLight;

/// <summary>
/// Developer testing only. When the LIGHT_TEST environment variable names a folder, .light reads
/// commands from cmd.txt in it ("open", "tab clips", "snap name"…) and draws its own window to PNG
/// files there. It never captures anything else on the screen. Off unless that variable is set.
/// </summary>
static class TestDriver
{
    public static void Start(NotchWindow w)
    {
        var dir = Environment.GetEnvironmentVariable("LIGHT_TEST");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var cmdFile = Path.Combine(dir, "cmd.txt");
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        poll.Tick += (_, _) =>
        {
            if (!File.Exists(cmdFile)) return;
            string[] lines;
            try { lines = File.ReadAllLines(cmdFile); File.Delete(cmdFile); } catch { return; }
            foreach (var line in lines)
            {
                var parts = line.Trim().Split(' ', 2);
                if (parts[0].Length == 0) continue;
                string arg = parts.Length > 1 ? parts[1] : "";
                string result;
                try { result = w.TestCommand(parts[0], arg, name => Snap(w, Path.Combine(dir, name + ".png"))); }
                catch (Exception ex) { result = "error: " + ex.Message; }
                File.AppendAllText(Path.Combine(dir, "log.txt"), $"{line} -> {result}\n");
            }
        };
        poll.Start();
    }

    static string Snap(Window w, string path)
    {
        // "snap ~name" keeps the background transparent (for promo pictures)
        bool clear = Path.GetFileName(path).StartsWith('~');
        if (clear) path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(path)[1..]);
        var root = (FrameworkElement)w.Content;
        double dpi = VisualTreeHelper.GetDpi(root).PixelsPerDip;
        int pw = (int)(root.ActualWidth * dpi * 2), ph = (int)(root.ActualHeight * dpi * 2);
        var layer = new RenderTargetBitmap(pw, ph, 192 * dpi, 192 * dpi, PixelFormats.Pbgra32);
        layer.Render(root);
        if (clear)
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(layer));
            using var f = File.Create(path);
            png.Save(f);
            return "saved";
        }
        // draw on a dark grey so the black island is visible in the picture
        var rtb = new RenderTargetBitmap(pw, ph, 192 * dpi, 192 * dpi, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)), null, r);
            dc.DrawImage(layer, r);
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        return "saved";
    }
}
