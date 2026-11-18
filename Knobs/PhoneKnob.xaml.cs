using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DotLight.Knobs;

/// <summary>Recent phone notifications, kept in memory only (gone when .light closes).</summary>
static class PhoneInbox
{
    public static readonly List<PhoneNote> All = new();
    public static PhoneLink? Phone;
    public static event Action? Changed;

    public static void Add(PhoneNote n) { All.Insert(0, n); if (All.Count > 30) All.RemoveAt(30); Changed?.Invoke(); }
    public static void SetPhone(PhoneLink? p) { Phone = p; Changed?.Invoke(); }

    public static void OpenPhoneLink()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\Microsoft.YourPhone_8wekyb3d8bbwe!App") { UseShellExecute = true }); }
        catch { }
    }
}

/// <summary>Phone tab: your phone's battery, recent notifications, and the iPhone clipboard setup.</summary>
public partial class PhoneKnob : UserControl
{
    /// <summary>"Copy 482913" was clicked on a notification.</summary>
    public event Action<string>? CopyCodeRequested;

    /// <summary>The iPhone clipboard switch was flipped; returns an error to show, or null.</summary>
    public Func<bool, string?>? SetClipListener;

    public bool InSetup => ClipView.Visibility == Visibility.Visible;

    public PhoneKnob()
    {
        InitializeComponent();
        PhoneInbox.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Build(); });
        IsVisibleChanged += (_, _) => { if (IsVisible) Build(); };
        Build();
    }

    public IEnumerable<FrameworkElement> RippleItems => Rows.Children.OfType<FrameworkElement>().Take(8);

    void OpenLink_Click(object sender, RoutedEventArgs e) => PhoneInbox.OpenPhoneLink();

    void Build()
    {
        var p = PhoneInbox.Phone;
        PhoneName.Text = p == null ? "Phone" : p.Name;
        PhoneLine.Text = p == null ? "Notifications from Phone Link"
            : p.Battery is int b ? $"Connected · battery {b}%" : "Connected";

        Rows.Children.Clear();
        EmptyHint.Visibility = PhoneInbox.All.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RowsBox.Visibility = PhoneInbox.All.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var n in PhoneInbox.All) Rows.Children.Add(Row(n));
    }

    FrameworkElement Row(PhoneNote n)
    {
        var badge = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 9, 0) };
        badge.Children.Add(new System.Windows.Shapes.Ellipse { Fill = n.IsCall ? Orange : Green });
        badge.Children.Add(new TextBlock
        {
            Text = n.IsCall ? "" : n.App.Length > 0 ? n.App[..1].ToUpperInvariant() : "•",
            FontFamily = n.IsCall ? (FontFamily)FindResource("Icons") : new FontFamily("Segoe UI"),
            FontSize = n.IsCall ? 9 : 10.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });

        var text = new StackPanel();
        var top = new DockPanel();
        var time = new TextBlock { Text = n.Time.ToString("HH:mm"), FontSize = 9.5, Margin = new Thickness(6, 1, 0, 0) };
        time.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(time, Dock.Right);
        top.Children.Add(time);
        top.Children.Add(new TextBlock
        {
            Text = n.IsCall ? $"Call · {n.Title}" : n.App == "Phone" ? n.Title : $"{n.App} · {n.Title}",
            FontWeight = FontWeights.SemiBold, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(top);
        if (n.Body.Length > 0)
            text.Children.Add(new TextBlock { Text = n.Body, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.85 });
        if (n.Code != null)
        {
            var copy = new Button { Style = (Style)FindResource("ChipBtn"), Content = $"Copy {n.Code}", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            copy.Click += (_, e) => { CopyCodeRequested?.Invoke(n.Code); e.Handled = true; };
            text.Children.Add(copy);
        }

        var dock = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        dock.Children.Add(badge);
        dock.Children.Add(text);
        var row = new Border { Style = (Style)FindResource("HoverRow"), Child = dock, ToolTip = "Click to open Phone Link" };
        row.MouseLeftButtonUp += (_, _) => PhoneInbox.OpenPhoneLink();
        return row;
    }

    static readonly Brush Green = Palette.LimeBrush, Orange = Palette.CoralBrush;
    static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    // ---------- iPhone clipboard setup ----------

    void ClipSetup_Click(object sender, RoutedEventArgs e) { ShowSetup(); }
    void Back_Click(object sender, RoutedEventArgs e) { ClipView.Visibility = Visibility.Collapsed; ListView.Visibility = Visibility.Visible; }

    public void ShowSetup()
    {
        ListView.Visibility = Visibility.Collapsed;
        ClipView.Visibility = Visibility.Visible;
        RefreshSetup();
    }

    void RefreshSetup()
    {
        bool on = Settings.Current.PhoneClipOn;
        ClipSwitch.IsChecked = on;
        ClipDetails.Opacity = on ? 1 : 0.45;
        AddressText.Text = PhoneClipListener.Addresses().FirstOrDefault() ?? "";
        AddressText.ToolTip = string.Join("\n", PhoneClipListener.Addresses()) + "\nIf the first doesn't work from your iPhone, try the number one.";
        CodeText.Text = Settings.Current.PhoneClipCode ?? "";
        CopyLinkBtn.Content = "Copy";
        CopyCodeBtn.Content = "Copy";
    }

    void ClipSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool on = ClipSwitch.IsChecked == true;
        if (on && string.IsNullOrEmpty(Settings.Current.PhoneClipCode)) Settings.Current.PhoneClipCode = PhoneClipListener.NewCode();
        Settings.Current.PhoneClipOn = on;
        Settings.Current.Save();
        var error = SetClipListener?.Invoke(on);
        ClipError.Text = error ?? "";
        ClipError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        RefreshSetup();
    }

    void NewCode_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.PhoneClipCode = PhoneClipListener.NewCode();
        Settings.Current.Save();
        RefreshSetup();
    }

    void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(AddressText.Text); CopyLinkBtn.Content = "Copied"; } catch { }
    }

    void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Settings.Current.PhoneClipCode ?? ""); CopyCodeBtn.Content = "Copied"; } catch { }
    }
}
