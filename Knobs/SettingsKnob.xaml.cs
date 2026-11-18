using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DotLight.Knobs;

/// <summary>Look knob: light colour, island style, features, shortcuts, position and start with Windows.</summary>
public partial class SettingsKnob : UserControl
{
    /// <summary>Raised when a spot is tapped, with its key (e.g. "BottomRight").</summary>
    public event Action<string>? PositionPicked;

    /// <summary>Raised when the island style, clock or breathing setting changes.</summary>
    public event Action? IslandLookChanged;

    /// <summary>Asked to use a new shortcut: (which, keys) → true if Windows accepted it.</summary>
    public Func<string, Hotkey, bool>? TryShortcut;

    /// <summary>True while waiting for keys, so the island's own shortcuts step aside.</summary>
    public event Action<bool>? Recording;

    public static readonly (string Key, string Label)[] IslandStyles =
        { ("Capsule", "Capsule"), ("Line", "Thin line"), ("Outline", "Outline"), ("Dot", "Dot") };

    /// <summary>Raised when a feature is switched on or off.</summary>
    public event Action? FeaturesChanged;

    /// <summary>The features that can be switched off: (label, read, write).</summary>
    static readonly (string Label, Func<bool> Get, Action<bool> Set)[] Features =
    {
        ("Snippets", () => Settings.Current.ShowSnippets, v => Settings.Current.ShowSnippets = v),
        ("File shelf", () => Settings.Current.ShowShelf, v => Settings.Current.ShowShelf = v),
        ("Grab image", () => Settings.Current.GrabImageOn, v => Settings.Current.GrabImageOn = v),
        ("Grab text", () => Settings.Current.GrabTextOn, v => Settings.Current.GrabTextOn = v),
        ("Focus timer", () => Settings.Current.ShowTimer, v => Settings.Current.ShowTimer = v),
        ("Notes", () => Settings.Current.ShowNotes, v => Settings.Current.ShowNotes = v),
        ("File lab", () => Settings.Current.ShowFileLab, v => Settings.Current.ShowFileLab = v),
        ("Screenshot moment", () => Settings.Current.ShotMoment, v => Settings.Current.ShotMoment = v),
        ("Camera and mic dots", () => Settings.Current.PrivacyDots, v => Settings.Current.PrivacyDots = v),
        ("Charging and battery", () => Settings.Current.PowerMoment, v => Settings.Current.PowerMoment = v),
        ("Download finished", () => Settings.Current.DownloadMoment, v => Settings.Current.DownloadMoment = v),
        ("Update notice", () => Settings.Current.UpdateNotice, v => Settings.Current.UpdateNotice = v),
        ("Phone notifications", () => Settings.Current.PhoneOn, v => Settings.Current.PhoneOn = v),
        ("Show phone message text", () => Settings.Current.PhoneShowMessage, v => Settings.Current.PhoneShowMessage = v),
        ("Pop for phone notifications", () => Settings.Current.PhonePop, v => Settings.Current.PhonePop = v),
    };
    readonly List<(ToggleButton Switch, Func<bool> Get)> featureSwitches = new();

    readonly Dictionary<string, RadioButton> spots = new();
    Button? recordingBox;

    public SettingsKnob()
    {
        InitializeComponent();
        BuildLights();
        BuildStyles();
        BuildFeatures();

        foreach (var (key, label) in NotchWindow.Positions)
        {
            int row = key.StartsWith("Top") ? 0 : key.StartsWith("Middle") ? 1 : 2;
            int col = key.EndsWith("Left") ? 0 : key.EndsWith("Right") ? 2 : 1;
            var spot = new RadioButton
            {
                Style = (Style)FindResource("Spot"),
                GroupName = "spot",
                Tag = key,
                ToolTip = label,
                VerticalAlignment = row switch { 0 => VerticalAlignment.Top, 1 => VerticalAlignment.Center, _ => VerticalAlignment.Bottom },
                HorizontalAlignment = col switch
                {
                    0 => HorizontalAlignment.Left,
                    2 => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Center,
                },
            };
            spot.Checked += (_, _) =>
            {
                SpotName.Text = label;
                if (key != Settings.Current.Position) PositionPicked?.Invoke(key);
            };
            Grid.SetRow(spot, row);
            Grid.SetColumn(spot, col);
            Spots.Children.Add(spot);
            spots[key] = spot;
        }

        PreviewKeyDown += OnRecordKey;
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopRecording(); };
        Refresh();
    }

    /// <summary>Shows the current settings (after a change from the tray menu or by dragging).</summary>
    /// <summary>Shows the "What's new" card at the top of Look (after an update).</summary>
    public void ShowWhatsNew(string version)
    {
        WhatsNewTitle.Text = $"What's new in {version}";
        WhatsNew.Visibility = Visibility.Visible;
        Scroller.ScrollToTop();
    }

    void WhatsNewClose_Click(object sender, RoutedEventArgs e) => WhatsNew.Visibility = Visibility.Collapsed;

    public void Refresh()
    {
        if (spots.TryGetValue(Settings.Current.Position, out var spot)) spot.IsChecked = true;
        StartupSwitch.IsChecked = Autostart.IsOn;
        ClockSwitch.IsChecked = Settings.Current.IdleClock;
        BreathSwitch.IsChecked = Settings.Current.Breathing;
        KeyOpen.Content = Settings.Current.HotkeyOpen;
        KeyGrab.Content = Settings.Current.HotkeyGrab;
        KeyImage.Content = Settings.Current.HotkeyGrabImage;
        KeyGrabRow.Visibility = Settings.Current.GrabTextOn ? Visibility.Visible : Visibility.Collapsed;
        KeyImageRow.Visibility = Settings.Current.GrabImageOn ? Visibility.Visible : Visibility.Collapsed;
        AgentSwitch.IsChecked = Settings.Current.AgentStatus;
        foreach (var (sw, get) in featureSwitches) sw.IsChecked = get();
    }

    // ---------- features ----------

    void BuildFeatures()
    {
        foreach (var (label, get, set) in Features)
        {
            var sw = new ToggleButton { Style = (Style)FindResource("Switch"), VerticalAlignment = VerticalAlignment.Center, IsChecked = get() };
            sw.Click += (_, _) =>
            {
                set(sw.IsChecked == true);
                Settings.Current.Save();
                Refresh();
                FeaturesChanged?.Invoke();
            };
            DockPanel.SetDock(sw, Dock.Right);
            var row = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
            row.Children.Add(sw);
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            FeatureRows.Children.Add(row);
            featureSwitches.Add((sw, get));
        }
    }

    /// <summary>Adds or removes .light's hook in Claude Code's settings (a backup is kept next to it).</summary>
    void Agent_Click(object sender, RoutedEventArgs e)
    {
        bool on = AgentSwitch.IsChecked == true;
        var error = on ? Agent.AddHook() : Agent.RemoveHook();
        if (error != null)
        {
            AgentSwitch.IsChecked = !on;
            AgentHint.Text = error;
            AgentHint.SetResourceReference(TextBlock.ForegroundProperty, "Orange");
        }
        else
        {
            Settings.Current.AgentStatus = on;
            Settings.Current.Save();
            AgentHint.Text = on
                ? "Added to Claude Code. Sessions you start from now on will show here."
                : "Removed from Claude Code's settings.";
            AgentHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            FeaturesChanged?.Invoke();
        }
        AgentHint.Visibility = Visibility.Visible;
    }

    /// <summary>The rows in order, for the ripple-in animation.</summary>
    public IEnumerable<FrameworkElement> RippleItems => Rows.Children.OfType<FrameworkElement>();

    // ---------- light ----------

    /// <summary>One round swatch per light colour; tapping one fades the whole island to it.</summary>
    void BuildLights()
    {
        foreach (var (key, label) in Theme.Choices)
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "light",
                Width = 25, Height = 25,
                Margin = new Thickness(0, 0, 3, 3),
                Background = Theme.Swatch(key),
                ToolTip = label,
                IsChecked = key == Settings.Current.Light,
            };
            swatch.Checked += (_, _) =>
            {
                LightName.Text = label;
                if (key == Settings.Current.Light) return;
                Settings.Current.Light = key;
                Settings.Current.Save();
                Theme.Apply();
            };
            Lights.Children.Add(swatch);
            if (key == Settings.Current.Light) LightName.Text = label;
        }
    }

    // ---------- island style ----------

    void BuildStyles()
    {
        foreach (var (key, label) in IslandStyles)
        {
            var tile = new RadioButton
            {
                Style = (Style)FindResource("StyleTile"),
                GroupName = "islandStyle",
                ToolTip = label,
                Content = StylePreview(key),
                IsChecked = key == Settings.Current.IslandStyle,
            };
            tile.Checked += (_, _) =>
            {
                StyleName.Text = label;
                if (key == Settings.Current.IslandStyle) return;
                Settings.Current.IslandStyle = key;
                Settings.Current.Save();
                IslandLookChanged?.Invoke();
            };
            Styles.Children.Add(tile);
            if (key == Settings.Current.IslandStyle) StyleName.Text = label;
        }
    }

    /// <summary>A tiny drawing of each resting island.</summary>
    static UIElement StylePreview(string key)
    {
        var g = new Grid();
        FrameworkElement shape = key switch
        {
            "Line" => new Border { Width = 34, Height = 3, CornerRadius = new CornerRadius(1.5) },
            "Dot" => new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4) },
            "Outline" => new Border { Width = 34, Height = 9, CornerRadius = new CornerRadius(4.5), Background = Brushes.Black, BorderThickness = new Thickness(1) },
            _ => new Border { Width = 34, Height = 9, CornerRadius = new CornerRadius(4.5), Background = Brushes.Black },
        };
        var b = (Border)shape;
        if (key is "Line" or "Dot") b.SetResourceReference(Border.BackgroundProperty, "Green");
        if (key == "Outline") b.SetResourceReference(Border.BorderBrushProperty, "Green");
        if (key == "Capsule") { b.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)); b.BorderThickness = new Thickness(0.5); }
        if (key is "Line" or "Dot")
        {
            var glow = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 8, Opacity = 0.8, Color = Theme.Current("Green") };
            Theme.Changed += () => glow.Color = Theme.Current("Green");
            b.Effect = glow;
        }
        shape.VerticalAlignment = VerticalAlignment.Top;
        shape.Margin = new Thickness(0, 6, 0, 0);
        g.Children.Add(shape);
        g.Children.Add(new TextBlock
        {
            Text = IslandStyles.First(s => s.Key == key).Label,
            FontSize = 9.5,
            Foreground = (Brush)Application.Current.Resources["Muted"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 5),
        });
        return g;
    }

    void Clock_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.IdleClock = ClockSwitch.IsChecked == true;
        Settings.Current.Save();
        IslandLookChanged?.Invoke();
    }

    void Breath_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.Breathing = BreathSwitch.IsChecked == true;
        Settings.Current.Save();
        IslandLookChanged?.Invoke();
    }

    // ---------- shortcuts ----------

    void Key_Click(object sender, RoutedEventArgs e)
    {
        var box = (Button)sender;
        if (recordingBox == box) { StopRecording(); return; }
        StopRecording();
        recordingBox = box;
        box.Content = "Press keys…";
        box.SetResourceReference(BorderBrushProperty, "Green");
        box.BorderThickness = new Thickness(1.5);
        KeyHint.Visibility = Visibility.Collapsed;
        Recording?.Invoke(true);
        Window.GetWindow(this)?.Activate();
        box.Focus();
    }

    void OnRecordKey(object sender, KeyEventArgs e)
    {
        if (recordingBox == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { StopRecording(); return; }
        if (Hotkey.IsModifier(key)) return; // wait for the actual key

        var h = new Hotkey(Keyboard.Modifiers, key);
        string which = (string)recordingBox.Tag;
        if (!h.IsValid)
        {
            ShowKeyHint("Use at least one of Ctrl, Alt, Shift or Win with a key.");
            return;
        }
        var current = new Dictionary<string, string>
        {
            ["open"] = Settings.Current.HotkeyOpen, ["grab"] = Settings.Current.HotkeyGrab, ["image"] = Settings.Current.HotkeyGrabImage,
        };
        if (current.Any(kv => kv.Key != which && Hotkey.Parse(kv.Value) == h))
        {
            ShowKeyHint($"{h} is already used by another .light shortcut.");
            return;
        }
        var box = recordingBox;
        StopRecording(); // turns the shortcuts back on before trying the new one
        if (TryShortcut?.Invoke(which, h) == true)
        {
            box.Content = h.ToString();
            KeyHint.Visibility = Visibility.Collapsed;
        }
        else ShowKeyHint($"{h} is taken by another app. Try a different one.");
    }

    void ShowKeyHint(string text)
    {
        KeyHint.Text = text;
        KeyHint.Visibility = Visibility.Visible;
    }

    void StopRecording()
    {
        if (recordingBox == null) return;
        recordingBox.BorderThickness = new Thickness(0);
        recordingBox = null;
        Refresh();
        Recording?.Invoke(false);
    }

    void ResetKeys_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        bool ok = TryShortcut?.Invoke("open", Hotkey.Parse("Alt+Space")!.Value) == true;
        if (Settings.Current.GrabTextOn) ok &= TryShortcut?.Invoke("grab", Hotkey.Parse("Ctrl+Alt+T")!.Value) == true;
        else Settings.Current.HotkeyGrab = "Ctrl+Alt+T";
        if (Settings.Current.GrabImageOn) ok &= TryShortcut?.Invoke("image", Hotkey.Parse("Ctrl+Alt+S")!.Value) == true;
        else Settings.Current.HotkeyGrabImage = "Ctrl+Alt+S";
        Settings.Current.Save();
        Refresh();
        if (!ok) ShowKeyHint("A default shortcut is taken by another app.");
        else KeyHint.Visibility = Visibility.Collapsed;
    }

    void Startup_Click(object sender, RoutedEventArgs e) => Autostart.Set(StartupSwitch.IsChecked == true);
}
