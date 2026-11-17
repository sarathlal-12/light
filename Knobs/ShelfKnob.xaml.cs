using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DotLight.Knobs;

/// <summary>
/// File shelf: files dropped on the island wait here (only a link is kept; nothing is copied).
/// Drag one out and Windows copies it to wherever you drop it.
/// </summary>
public partial class ShelfKnob : UserControl
{
    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };
    Point downAt;

    /// <summary>True while a file is being dragged out, so the island stays open.</summary>
    public bool DraggingOut { get; private set; }

    public ShelfKnob()
    {
        InitializeComponent();
        ShelfStore.All.CollectionChanged += (_, _) => Rebuild();
        IsVisibleChanged += (_, _) => { if (IsVisible) Rebuild(); }; // files may have moved meanwhile
        Rebuild();
    }

    public IEnumerable<FrameworkElement> RippleItems => List.Items.OfType<FrameworkElement>();

    void Rebuild()
    {
        List.Items.Clear();
        foreach (var item in ShelfStore.All) List.Items.Add(Tile(item));
        bool any = ShelfStore.All.Count > 0;
        EmptyHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        DragAll.Visibility = ShelfStore.All.Count(i => i.Exists) > 1 ? Visibility.Visible : Visibility.Collapsed;
        ClearBtn.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A square tile: thumbnail or file icon, the name, and a remove button on hover.</summary>
    FrameworkElement Tile(ShelfItem item)
    {
        bool exists = item.Exists;
        var face = new Grid { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 7, 0, 0) };
        var pic = Picture(item.Path);
        if (pic != null) face.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } });
        else face.Children.Add(new TextBlock
        {
            FontFamily = (FontFamily)FindResource("Icons"), FontSize = 22,
            Text = Directory.Exists(item.Path) ? "" : "",
            Foreground = (Brush)FindResource("Muted"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });

        var name = new TextBlock
        {
            Text = item.Name, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(4, 5, 4, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var remove = new Button
        {
            Style = (Style)FindResource("IconBtn"), Width = 18, Height = 18, FontSize = 8, Content = "",
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0),
            ToolTip = "Remove from shelf", Opacity = 0,
        };
        remove.Click += (_, e) => { ShelfStore.All.Remove(item); ShelfStore.Save(); e.Handled = true; };

        var body = new StackPanel();
        body.Children.Add(face);
        body.Children.Add(name);
        var grid = new Grid();
        grid.Children.Add(body);
        grid.Children.Add(remove);

        var tile = new Border
        {
            Width = 72, Height = 70, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("Surface"), Child = grid, Tag = item, Cursor = Cursors.Hand,
            Opacity = exists ? 1 : 0.35,
            ToolTip = exists ? item.Path + "\nDrag out to copy · double-click to open" : item.Path + "\nMoved or deleted",
        };
        tile.MouseEnter += (_, _) => { remove.Opacity = 1; tile.Background = (Brush)FindResource("SurfaceHi"); };
        tile.MouseLeave += (_, _) => { remove.Opacity = 0; tile.Background = (Brush)FindResource("Surface"); };
        tile.PreviewMouseLeftButtonDown += Item_Down;
        tile.PreviewMouseMove += (s, e) => { if (exists) DragOut(e, tile, item.Path); };
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && exists)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Path) { UseShellExecute = true }); } catch { }
        };
        return tile;
    }

    void Item_Down(object sender, MouseButtonEventArgs e) => downAt = e.GetPosition(this);

    void All_Move(object sender, MouseEventArgs e) =>
        DragOut(e, DragAll, ShelfStore.All.Where(i => i.Exists).Select(i => i.Path).ToArray());

    void DragOut(MouseEventArgs e, DependencyObject source, params string[] paths)
    {
        if (e.LeftButton != MouseButtonState.Pressed || paths.Length == 0 || DraggingOut) return;
        var d = e.GetPosition(this) - downAt;
        if (Math.Abs(d.X) < 5 && Math.Abs(d.Y) < 5) return;
        DraggingOut = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths);
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        }
        catch { }
        finally { DraggingOut = false; }
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        ShelfStore.All.Clear();
        ShelfStore.Save();
    }

    /// <summary>A small picture for the tile: a thumbnail for images, else the file's own icon.</summary>
    static ImageSource? Picture(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant()) && new FileInfo(path).Length < 40_000_000)
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(path);
                img.DecodePixelWidth = 80;
                img.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                img.EndInit();
                img.Freeze();
                return img;
            }
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }
}
