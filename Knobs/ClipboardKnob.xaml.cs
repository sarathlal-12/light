using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DotLight.Knobs;

public class ClipItem : INotifyPropertyChanged
{
    public string Kind { get; set; } = "text"; // "text" or "image"
    public string Text { get; set; } = "";
    public string? ImageFile { get; set; }

    // What the text turned out to be (link, colour, path…) and what you can do with it. Worked out once, locally.
    SmartInfo? smart;
    bool smartChecked;
    [JsonIgnore]
    public SmartInfo? Smart
    {
        get
        {
            if (!smartChecked) { smartChecked = true; smart = IsText ? SmartClip.Detect(Text) : null; }
            return smart;
        }
    }
    [JsonIgnore] public IReadOnlyList<SmartAction> Actions => Smart?.Actions ?? Array.Empty<SmartAction>();
    [JsonIgnore] public bool HasActions => Actions.Count > 0;
    [JsonIgnore] public Brush? SwatchBrush => Smart?.Swatch is Color c ? new SolidColorBrush(c) : null;
    [JsonIgnore] public bool HasSwatch => Smart?.Swatch != null;
    public string? Hash { get; set; }

    bool highlighted;
    [JsonIgnore]
    public bool IsHighlighted
    {
        get => highlighted;
        set { highlighted = value; Changed(nameof(IsHighlighted)); }
    }

    bool pinned;
    public bool Pinned
    {
        get => pinned;
        set { pinned = value; Changed(nameof(Pinned)); Changed(nameof(PinGlyph)); Changed(nameof(PinOpacity)); }
    }

    DateTime time;
    public DateTime Time
    {
        get => time;
        set { time = value; Changed(nameof(Time)); Changed(nameof(TimeLabel)); }
    }

    [JsonIgnore] public bool IsText => Kind == "text";
    [JsonIgnore] public bool IsImage => Kind == "image";
    [JsonIgnore] public string Preview => (Text.Length > 300 ? Text[..300] : Text).Trim();
    [JsonIgnore] public string PinGlyph => Pinned ? "" : "";
    [JsonIgnore] public double PinOpacity => Pinned ? 1 : 0.5;
    [JsonIgnore] public string TimeLabel => Time.Date == DateTime.Today ? Time.ToString("HH:mm") : Time.ToString("d MMM, HH:mm");

    ImageSource? thumb;
    [JsonIgnore]
    public ImageSource? Thumb => thumb ??= ClipboardKnob.LoadImage(ImageFile, decodeHeight: 128);

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Clipboard knob: history of copied text and images, with pins and search.</summary>
public partial class ClipboardKnob : UserControl
{
    const int MaxClips = 50;
    static string ClipDir => Path.Combine(Settings.Dir, "clips");
    static string StoreFile => Path.Combine(Settings.Dir, "clips.json");

    readonly ObservableCollection<ClipItem> items;
    readonly ICollectionView view;
    DateTime ignoreUntil;

    /// <summary>Raised after you click a clip, so the notch can close and you can paste.</summary>
    public event Action? Copied;

    /// <summary>Raised when something new is copied anywhere on the PC, with what it turned out to be (or null).</summary>
    public event Action<SmartInfo?>? Captured;

    /// <summary>Raised just before Captured when the new copy is an image (a screenshot, say).</summary>
    public event Action<ClipItem>? ImageCaptured;

    /// <summary>Raised when a highlighted clip should be pasted straight into the app you came from.</summary>
    public event Action? PasteRequested;

    /// <summary>"Grab text from screen" was clicked.</summary>
    public event Action? GrabRequested;

    void Grab_Click(object sender, RoutedEventArgs e) => GrabRequested?.Invoke();

    /// <summary>"Grab image from screen" was clicked.</summary>
    public event Action? GrabImageRequested;

    void GrabImage_Click(object sender, RoutedEventArgs e) => GrabImageRequested?.Invoke();

    /// <summary>Shows only the grab buttons whose feature is on.</summary>
    public void ShowGrabButtons(bool text, bool image)
    {
        GrabTextBtn.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        GrabImageBtn.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The first visible rows, for the ripple-in animation.</summary>
    public IEnumerable<FrameworkElement> RippleItems =>
        Enumerable.Range(0, Math.Min(List.Items.Count, 10))
            .Select(i => List.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)
            .Where(c => c != null)!;

    ClipItem? highlighted;

    public ClipboardKnob()
    {
        InitializeComponent();
        items = new ObservableCollection<ClipItem>(Load());
        view = CollectionViewSource.GetDefaultView(items);
        view.SortDescriptions.Add(new SortDescription(nameof(ClipItem.Pinned), ListSortDirection.Descending));
        view.SortDescriptions.Add(new SortDescription(nameof(ClipItem.Time), ListSortDirection.Descending));
        view.Filter = Matches;
        List.ItemsSource = view;
        PauseBtn.IsChecked = Settings.Current.ClipsPaused;
        UpdateHint();
    }

    // ---------- watching the clipboard ----------

    public async void OnClipboardChanged()
    {
        if (DateTime.Now < ignoreUntil) return;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try { Capture(); return; }
            catch (ExternalException) { await Task.Delay(60); } // another app still has the clipboard open
            catch (Exception) { return; }
        }
    }

    void Capture()
    {
        var data = Clipboard.GetDataObject();
        if (data == null || Settings.Current.ClipsPaused) return;

        // Respect password managers and apps that ask not to be recorded.
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") ||
            data.GetDataPresent("Clipboard Viewer Ignore"))
            return;
        if (data.GetDataPresent("CanIncludeInClipboardHistory") &&
            data.GetData("CanIncludeInClipboardHistory") is MemoryStream flag &&
            flag.Length >= 4 && BitConverter.ToInt32(flag.ToArray(), 0) == 0)
            return;

        if (data.GetDataPresent(DataFormats.UnicodeText))
        {
            if (data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text))
            {
                AddClipText(text);
                Captured?.Invoke(SmartClip.Detect(text));
            }
        }
        else if (data.GetDataPresent(DataFormats.Bitmap))
        {
            if (Clipboard.GetImage() is { } img)
            {
                var clip = AddImage(img);
                ImageCaptured?.Invoke(clip);
                Captured?.Invoke(null);
            }
        }
    }

    void AddClipText(string text)
    {
        var existing = items.FirstOrDefault(c => c.IsText && c.Text == text);
        if (existing != null) existing.Time = DateTime.Now;
        else items.Add(new ClipItem { Kind = "text", Text = text, Time = DateTime.Now });
        Trim();
        Changed();
    }

    ClipItem AddImage(BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var bytes = ms.ToArray();
        var hash = Convert.ToHexString(SHA1.HashData(bytes));

        var existing = items.FirstOrDefault(c => c.Hash == hash);
        if (existing != null)
        {
            existing.Time = DateTime.Now;
        }
        else
        {
            Directory.CreateDirectory(ClipDir);
            var file = Path.Combine(ClipDir, hash[..16] + ".png");
            File.WriteAllBytes(file, bytes);
            existing = new ClipItem { Kind = "image", ImageFile = file, Hash = hash, Time = DateTime.Now };
            items.Add(existing);
            Trim();
        }
        Changed();
        return existing;
    }

    void Trim()
    {
        foreach (var old in items.Where(c => !c.Pinned).OrderByDescending(c => c.Time).Skip(MaxClips).ToList())
            Remove(old);
    }

    void Remove(ClipItem c)
    {
        items.Remove(c);
        if (c.ImageFile != null && items.All(o => o.ImageFile != c.ImageFile))
            try { File.Delete(c.ImageFile); } catch { }
    }

    // ---------- clicks ----------

    void Item_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (justDragged) { justDragged = false; return; }
        if (((FrameworkElement)sender).DataContext is not ClipItem c) return;
        if (!PutOnClipboard(c)) return;
        Hint.Text = "Copied. Paste it anywhere with Ctrl+V.";
        Copied?.Invoke();
    }

    bool PutOnClipboard(ClipItem c)
    {
        ignoreUntil = DateTime.Now.AddMilliseconds(700);
        try
        {
            if (c.IsText) Clipboard.SetDataObject(c.Text, true);
            else if (LoadImage(c.ImageFile) is BitmapSource img) Clipboard.SetImage(img);
        }
        catch (ExternalException)
        {
            Hint.Text = "The clipboard is busy, try again.";
            return false;
        }
        c.Time = DateTime.Now;
        Changed();
        return true;
    }

    // ---------- drag a clip out into any app ----------

    Point downAt;
    bool justDragged;

    void Item_Down(object sender, System.Windows.Input.MouseButtonEventArgs e) { downAt = e.GetPosition(this); justDragged = false; }

    void Item_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || DraggingOut) return;
        var d = e.GetPosition(this) - downAt;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (((FrameworkElement)sender).DataContext is not ClipItem c) return;
        justDragged = true;
        DragOut((DependencyObject)sender, c);
    }

    /// <summary>True while a clip is being dragged out, so .light doesn't take it as a drop onto itself.</summary>
    public static bool DraggingOut { get; private set; }

    /// <summary>
    /// Drags a clip out. Images go as a PNG file (Explorer, chat apps, browsers) and as the picture itself
    /// (Paint, Word, editors), so whatever you drop it on takes the form it understands.
    /// </summary>
    public static void DragOut(DependencyObject source, ClipItem c)
    {
        var data = new DataObject();
        if (c.IsText) data.SetText(c.Text);
        else
        {
            if (DragCopy(c) is string file)
            {
                data.SetFileDropList(new StringCollection { file });
                try { data.SetData("PNG", new MemoryStream(File.ReadAllBytes(file))); } catch { }
            }
            if (LoadImage(c.ImageFile) is BitmapSource img) data.SetImage(img);
        }
        DraggingOut = true;
        try { DragDrop.DoDragDrop(source, data, DragDropEffects.Copy); }
        catch { }
        finally { DraggingOut = false; }
    }

    /// <summary>A copy of the image with a readable name ("Screenshot 2026-11-18 154012.png") in a temp folder.</summary>
    static string? DragCopy(ClipItem c)
    {
        if (c.ImageFile == null || !File.Exists(c.ImageFile)) return null;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), ".light drag");
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.EnumerateFiles(dir))
                try { if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-1)) File.Delete(old); } catch { }
            var file = Path.Combine(dir, $"Screenshot {c.Time:yyyy-MM-dd HHmmss}.png");
            if (!File.Exists(file)) File.Copy(c.ImageFile, file);
            return file;
        }
        catch { return null; }
    }

    // ---------- pause ----------

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.ClipsPaused = PauseBtn.IsChecked == true;
        Settings.Current.Save();
        UpdateHint();
    }

    // ---------- smart actions ----------

    /// <summary>Runs a smart action: copy a converted version, open a link/folder/mail, or show a file.</summary>
    public void Run(SmartAction a)
    {
        try
        {
            if (a.CopyText != null) CopyText(a.CopyText);
            else if (a.Open != null) Process.Start(new ProcessStartInfo(a.Open) { UseShellExecute = true });
            else if (a.Reveal != null) Process.Start("explorer.exe", $"/select,\"{a.Reveal}\"");
        }
        catch { Hint.Text = "Couldn't do that."; }
    }

    /// <summary>Puts new text on the clipboard (e.g. a cleaned link) and keeps it in the history.</summary>
    void CopyText(string text)
    {
        ignoreUntil = DateTime.Now.AddMilliseconds(700);
        try { Clipboard.SetDataObject(text, true); }
        catch (ExternalException) { Hint.Text = "The clipboard is busy, try again."; return; }
        AddClipText(text);
        Hint.Text = "Copied. Paste it anywhere with Ctrl+V.";
    }

    void Action_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SmartAction a) return;
        Run(a);
        if (a.CopyText != null) Copied?.Invoke();
    }

    // ---------- the clipboard dial ----------

    /// <summary>Mouse wheel over the knob: move the highlight through your clips (positive = older).</summary>
    public void Flip(int steps)
    {
        var list = view.Cast<ClipItem>().ToList();
        if (list.Count == 0) return;
        int at = highlighted == null ? -1 : list.IndexOf(highlighted);
        int next = Math.Clamp(at + steps, 0, list.Count - 1);
        if (at == -1 && steps < 0) next = 0;
        Highlight(list[next]);
        Hint.Text = "Enter pastes it  ·  click to copy";
    }

    void Highlight(ClipItem? c)
    {
        if (highlighted != null) highlighted.IsHighlighted = false;
        highlighted = c;
        if (c == null) return;
        c.IsHighlighted = true;
        (List.ItemContainerGenerator.ContainerFromItem(c) as FrameworkElement)?.BringIntoView();
    }

    /// <summary>Enter: copy the highlighted clip and paste it into the app you were using.</summary>
    public bool PasteHighlighted()
    {
        if (highlighted == null || !PutOnClipboard(highlighted)) return false;
        Highlight(null);
        UpdateHint();
        PasteRequested?.Invoke();
        return true;
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ClipItem c) { c.Pinned = !c.Pinned; Changed(); }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ClipItem c) { Remove(c); Changed(); }
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in items.Where(c => !c.Pinned).ToList()) Remove(c);
        Changed();
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        view?.Refresh();
    }

    bool Matches(object o)
    {
        var q = Search.Text.Trim();
        if (q.Length == 0) return true;
        return o is ClipItem { IsText: true } c && c.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- storage ----------

    void Changed()
    {
        view.Refresh();
        Save();
        UpdateHint();
    }

    void UpdateHint() =>
        Hint.Text = Settings.Current.ClipsPaused ? "Paused: new copies aren't saved. Click pause again to resume."
            : items.Count == 0
            ? "Copy something and it shows up here."
            : $"{items.Count} clips  ·  click to copy, drag into any app";

    static string BackupFile => StoreFile + ".bak";

    /// <summary>
    /// Loads the history. If the file can't be read it is set aside as clips.broken-*.json (never
    /// overwritten), and the previous good copy (clips.json.bak) is tried instead.
    /// </summary>
    static List<ClipItem> Load()
    {
        foreach (var file in new[] { StoreFile, BackupFile })
        {
            if (!File.Exists(file)) continue;
            try
            {
                return (JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(file)) ?? new())
                    .Where(c => c.IsText || File.Exists(c.ImageFile))
                    .ToList();
            }
            catch
            {
                try { File.Copy(file, Path.Combine(Settings.Dir, $"clips.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true); }
                catch { }
            }
        }
        return new();
    }

    /// <summary>
    /// Saves safely: writes a temp file first, keeps the previous version as clips.json.bak, then swaps
    /// it in, so a crash or a bad write can never leave you with an empty history.
    /// </summary>
    void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            var temp = StoreFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items.ToList()));
            if (File.Exists(StoreFile)) File.Replace(temp, StoreFile, BackupFile, ignoreMetadataErrors: true);
            else File.Move(temp, StoreFile);
        }
        catch { }
    }

    internal static BitmapSource? LoadImage(string? file, int decodeHeight = 0)
    {
        if (file == null || !File.Exists(file)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            bmp.UriSource = new Uri(file);
            if (decodeHeight > 0) bmp.DecodePixelHeight = decodeHeight;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
