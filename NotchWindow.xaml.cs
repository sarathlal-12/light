using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using DotLight.Knobs;
using WF = System.Windows.Forms;

namespace DotLight;

/// <summary>
/// The .light island: a black capsule floating at the top centre of the screen, in the spirit of
/// the iPhone's Dynamic Island. It morphs on springs between a tiny idle pill, live moments
/// (music, new song, copied) and the full panel with the knobs.
/// </summary>
public partial class NotchWindow : Window
{
    enum Island { Idle, Music, Song, Copied, Smart, Dial, Timer, Alarm, Agent, AgentDone, Shot, Phone, PhoneCode, Call, Open }

    const double Gap = 3, SideGap = 10, MaxRadius = 34;

    public static readonly (string Key, string Label)[] Positions =
    {
        ("TopLeft", "Top left"), ("TopCenter", "Top centre"), ("TopRight", "Top right"),
        ("MiddleLeft", "Left middle"), ("MiddleRight", "Right middle"),
        ("BottomLeft", "Bottom left"), ("BottomCenter", "Bottom centre"), ("BottomRight", "Bottom right"),
    };
    const int HotkeyToggle = 1, HotkeyGrab = 2, HotkeyGrabImage = 3;

    double copiedWidth = 130, noteWidth = 160, agentDoneWidth = 150;
    Note? pinnedNote;

    /// <summary>Size of each shape. On the left/right middle spots the small resting shapes stand upright.</summary>
    /// <summary>Extra width on the right while the camera or mic dots show.</summary>
    double DotRoom => privacyShown && !AtMiddle ? 16 : 0;

    Size SizeOf(Island s)
    {
        var size = BaseSizeOf(s);
        if (s != Island.Open) size.Width += DotRoom;
        return size;
    }

    Size BaseSizeOf(Island s)
    {
        string style = Settings.Current.IslandStyle;
        if (s == Island.Idle)
        {
            if (privacyShown && style is "Line" or "Dot") return new Size(22, 18); // a small pill for the camera/mic dots
            if (style == "Line") return AtMiddle ? new Size(4, 120) : new Size(120, 4);
            if (style == "Dot") return new Size(12, 12);
            if (!AtMiddle && pinnedNote != null) return new Size(noteWidth, 20);
            return AtMiddle ? new Size(20, 110) : new Size(110, 20);
        }
        if (AtMiddle && s is Island.Music or Island.Copied or Island.Timer)
            return s switch
            {
                Island.Music => new Size(20, 150),
                Island.Timer => new Size(20, 110),
                _ => new Size(20, 64),
            };
        return s switch
        {
            Island.Music => new Size(150, 20),
            Island.Song => new Size(300, 54),
            Island.Copied => new Size(copiedWidth, 32),
            Island.Dial => new Size(240, 32),
            Island.Smart => new Size(300, 40),
            Island.Timer => new Size(172, 28),
            Island.Alarm => new Size(330, 136),
            Island.Agent => new Size(370, 152),
            Island.AgentDone => new Size(agentDoneWidth, 32),
            Island.Shot => new Size(360, 136),
            Island.Phone => new Size(320, 52),
            Island.PhoneCode => new Size(350, 140),
            Island.Call => new Size(380, 112),
            _ => new Size(340, 264),
        };
    }

    // Width settles a touch faster than height, which gives the morph its organic feel.
    readonly Spring width = new(110, stiffness: 640, damping: 40);
    readonly Spring height = new(20, stiffness: 560, damping: 36);

    readonly DispatcherTimer collapseDelay = new() { Interval = TimeSpan.FromMilliseconds(260) };
    readonly DispatcherTimer edgeWatch = new() { Interval = TimeSpan.FromMilliseconds(25) };
    readonly DispatcherTimer momentEnd = new();
    readonly DispatcherTimer panelDialHide = new() { Interval = TimeSpan.FromMilliseconds(1100) };
    DateTime openedAt;
    double wheelRest;

    // Rim light: follows the live sound level while music plays, flashes on moments.
    readonly DispatcherTimer glowTick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    double glow, glowFlash; // leftover from touchpads that scroll in small fractions of a notch
    IntPtr hwnd, previousForeground;
    Island state = Island.Idle;
    bool musicPlaying, openedByKey, animating, dragging;
    Native.POINT dragStart;
    Point windowStart;
    double dpi = 1;
    TimeSpan lastFrame;

    public bool HotkeyOk { get; private set; }
    public bool GrabHotkeyOk { get; private set; }
    public bool GrabImageHotkeyOk { get; private set; }
    bool recordingKeys;

    readonly DispatcherTimer clockTick = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer alarmPulse = new() { Interval = TimeSpan.FromMilliseconds(1100) };
    DateTime alarmAt;
    bool alarmPending; // timer finished while the panel was open: ring when it closes
    string? pendingCopiedLabel;
    string currentTab = "music";

    // Steps aside while a game, fullscreen video or presentation is in front.
    readonly DispatcherTimer fullscreenWatch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer trimSoon = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly DispatcherTimer trimIdle = new() { Interval = TimeSpan.FromSeconds(60) };
    bool fullscreenHidden, ignoreFullscreen;

    /// <summary>Set LIGHT_FRAME_LOG to a file path to record animation frames (for tuning smoothness).</summary>
    static readonly string? FrameLog = Environment.GetEnvironmentVariable("LIGHT_FRAME_LOG");
    bool IsOpen => state == Island.Open;

    /// <summary>Raised when the island moves to another spot (tray menu, settings knob or drag).</summary>
    public event Action? PositionChanged;

    static string Pos => Settings.Current.Position;
    static bool AtBottom => Pos.StartsWith("Bottom");
    static bool AtMiddle => Pos.StartsWith("Middle");
    static HorizontalAlignment Side =>
        Pos.EndsWith("Left") ? HorizontalAlignment.Left :
        Pos.EndsWith("Right") ? HorizontalAlignment.Right : HorizontalAlignment.Center;
    // Music shows once (the "now playing" moment) and then the island rests; hover to see the player.
    Island Resting => agentNeeds.Count > 0 ? Island.Agent : FocusTimer.Active ? Island.Timer : Island.Idle;

    DateTime lastSound = DateTime.Now;
    bool musicSilent;

    public NotchWindow()
    {
        InitializeComponent();
        BuildWave(CompactWave, 4, 9);
        BuildWave(MomentWave, 4, 14);
        BuildWave(Music.WavePanel, 4, 11);

        // Opens the instant the cursor reaches the island or the top edge.
        collapseDelay.Tick += (_, _) => { collapseDelay.Stop(); if (!ShouldStayOpen()) Collapse(); };
        edgeWatch.Tick += (_, _) => { if (!IsOpen && !fullscreenHidden && state is not (Island.Dial or Island.Smart or Island.Alarm or Island.Agent or Island.Shot or Island.Phone or Island.PhoneCode or Island.Call) && !dragging && CursorAtEdge()) Expand(byKey: false); };

        // Resting face: the clock ticks over, a pinned note shows instead.
        clockTick.Tick += (_, _) => UpdateClock();
        clockTick.Start();
        pinnedNote = NoteStore.PinnedNote;
        Notes.PinnedChanged += () => { UpdateIdleFace(); if (state == Island.Idle) Morph(Island.Idle); };
        Look.IslandLookChanged += () => { UpdateIdleFace(); Morph(state); };

        // Shortcuts can be changed in the Look knob.
        Look.TryShortcut = TryShortcut;
        Look.Recording += on => { recordingKeys = on; if (on) UnregisterHotkeys(); else RegisterHotkeys(); };

        // Focus timer: the island counts down, then rings.
        FocusTimer.Changed += OnTimerChanged;
        FocusTimer.Finished += OnTimerFinished;
        alarmPulse.Tick += (_, _) =>
        {
            if (state != Island.Alarm && !alarmPending) { alarmPulse.Stop(); return; }
            FlashGlow(1);
            if (DateTime.Now - alarmAt > TimeSpan.FromMinutes(1)) DismissAlarm();
        };

        Clips.GrabRequested += () => _ = StartGrab();
        Clips.GrabImageRequested += () => _ = StartGrabImage();
        Snippets.PasteRequested += PasteSnippet;
        Look.FeaturesChanged += ApplyFeatures;

        // Claude Code status: working / needs your OK / done.
        Agent.Received += OnAgent;
        Agent.Listen(Dispatcher);
        if (Settings.Current.AgentStatus && Environment.GetEnvironmentVariable("LIGHT_TEST") == null && !Agent.HookInstalled)
            Agent.AddHook(); // an older .light added fewer events: bring the hook up to date
        Claude.ShowRequested += ShowSession;

        // Camera/mic dots, charging, finished downloads and new versions.
        privacyWatch.Tick += (_, _) => UpdatePrivacy();
        privacyWatch.Start();
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.StatusChange) Dispatcher.BeginInvoke(CheckPower); };
        batteryWatch.Tick += (_, _) => CheckPower();
        batteryWatch.Start();
        lastCharging = Power.Charging;
        updateWatch.Tick += (_, _) => { updateWatch.Interval = TimeSpan.FromHours(6); _ = CheckUpdate(); };
        updateWatch.Start();
        whatsNewSoon.Tick += (_, _) => { whatsNewSoon.Stop(); AnnounceUpdate(); };
        whatsNewSoon.Start();

        // Phone: notifications and calls through Phone Link, the phone's battery, and the iPhone clipboard.
        phoneWatch.Tick += (_, _) => CheckPhoneNotes();
        phoneWatch.Start();
        phoneStatusWatch.Tick += (_, _) => { phoneStatusWatch.Interval = TimeSpan.FromSeconds(20); _ = CheckPhone(); };
        phoneStatusWatch.Start();
        PhoneK.CopyCodeRequested += code => { Collapse(); CopyCode(code); };
        PhoneK.SetClipListener = on => { if (on) return phoneClip.Start(); phoneClip.Stop(); return null; };
        phoneClip.Received += text => Dispatcher.BeginInvoke(() => FromPhoneClipboard(text));
        if (Settings.Current.PhoneClipOn && Environment.GetEnvironmentVariable("LIGHT_TEST") == null) phoneClip.Start();
        agentPulse.Tick += (_, _) => { if (state == Island.Agent) FlashGlow(0.8); else agentPulse.Stop(); };
        Theme.Changed += () => TabLightGlow.Color = TabColour(currentTab);
        fullscreenWatch.Tick += (_, _) => { CheckFullscreen(); CheckAgentSeen(); };
        fullscreenWatch.Start();
        edgeWatch.Start();

        // Give memory back to Windows a few seconds after the panel closes, and now and then while idle.
        trimSoon.Tick += (_, _) => { trimSoon.Stop(); if (!IsOpen) MemoryTrim.Now(); };
        trimIdle.Tick += (_, _) => { if (!IsOpen && !Shelf.DraggingOut) MemoryTrim.Now(); };
        trimIdle.Start();
        trimSoon.Start();
        momentEnd.Tick += (_, _) => { momentEnd.Stop(); if (state is Island.Song or Island.Copied or Island.Smart or Island.Dial or Island.AgentDone or Island.Shot or Island.Phone or Island.PhoneCode or Island.Call) { momentDragFile = null; Morph(Resting); } };
        panelDialHide.Tick += (_, _) => { panelDialHide.Stop(); PanelDialBox.BeginAnimation(OpacityProperty, Ease(0, 220)); };

        // Mouse wheel = turning a knob (volume, Shift = seek, clipboard = flip through clips).
        PreviewMouseWheel += OnWheel;
        glowTick.Tick += (_, _) => UpdateGlow();
        // After using the dial on the closed island, a click opens it. A ringing timer is dismissed instead.
        Shell.MouseLeftButtonUp += (_, _) =>
        {
            if (draggedOut) { draggedOut = false; return; }
            if (state == Island.Alarm) DismissAlarm();
            else if (state == Island.Agent) ShowAgentWindow();
            else if (state == Island.Shot) { Expand(byKey: false); ShowKnob("clips"); }
            else if (state is Island.Phone or Island.PhoneCode or Island.Call) { PhoneInbox.OpenPhoneLink(); Morph(Resting); }
            else if (!IsOpen) Expand(byKey: false);
        };
        // Drag a screenshot or a finished download straight off the island.
        Shell.PreviewMouseLeftButtonDown += (_, e) => { pressAt = e.GetPosition(this); draggedOut = false; };
        Shell.PreviewMouseMove += OnMomentDrag;
        Clips.PasteRequested += PasteToPreviousApp;

        Shell.MouseEnter += (_, _) =>
        {
            collapseDelay.Stop();
            if (state is Island.Smart or Island.Shot or Island.Phone or Island.PhoneCode or Island.Call) { momentEnd.Stop(); return; } // hold it so it can be clicked or dragged
            if (state is Island.Alarm or Island.Agent) return;
            if (!IsOpen) Expand(byKey: false);
        };
        Shell.MouseLeave += (_, _) =>
        {
            if (IsOpen) collapseDelay.Start();
            else if (state is Island.Smart or Island.Shot or Island.Phone or Island.PhoneCode or Island.Call) { momentEnd.Interval = TimeSpan.FromMilliseconds(1500); momentEnd.Start(); }
        };

        Deactivated += (_, _) => { if (IsOpen && !App.DialogOpen) Collapse(); };
        PreviewKeyDown += (_, e) =>
        {
            if (recordingKeys) return; // the Look knob is listening for a new shortcut
            if (e.Key == Key.Escape && Notes.Visibility == Visibility.Visible && Notes.Editing) return; // back to the list first
            if (e.Key == Key.Escape && Snippets.Visibility == Visibility.Visible && Snippets.Editing) return;
            if (e.Key == Key.Escape) { Collapse(); e.Handled = true; }
            else if (e.Key == Key.Enter && Clips.Visibility == Visibility.Visible && Clips.PasteHighlighted()) e.Handled = true;
        };

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => { collapseDelay.Start(); dropZonesHide.Stop(); dropZonesHide.Start(); };
        dropZonesHide.Tick += (_, _) => { dropZonesHide.Stop(); DropZones.Visibility = Visibility.Collapsed; };
        Drop += OnDrop;

        Music.NowPlayingChanged += OnNowPlaying;
        Music.TrackChanged += OnTrackChanged;
        Clips.ImageCaptured += clip => shotPending = Settings.Current.ShotMoment ? clip : null;
        Clips.Captured += info =>
        {
            if (shotPending != null) { ShowShot(shotPending); shotPending = null; pendingCopiedLabel = null; return; }
            if (pendingCopiedLabel != null) { ShowCopied(pendingCopiedLabel); pendingCopiedLabel = null; } // text grabbed from the screen
            else if (info != null) ShowSmart(info);
            else ShowCopied();
        };
        Clips.Copied += () => { Collapse(); ShowCopied(); };

        Loaded += (_, _) =>
        {
            var start = SizeOf(state);
            width.Value = width.Target = start.Width;
            height.Value = height.Target = start.Height;
            Place(); ApplyFeatures(); ColourTabs();
            UpdateIdleFace();
            SwapContent(null, LayerOf(state));
            UpdateIdleLook();
            Full.Effect = new BlurEffect { Radius = 1 };
            Dispatcher.BeginInvoke(() => { Full.Effect = null; MoveTabLight(animate: false); }, DispatcherPriority.ContextIdle);
        };
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => Place());

        // Drag the open island by its top bar; it snaps to the nearest of the six spots.
        Header.MouseLeftButtonDown += Header_Down;
        Header.MouseMove += Header_Move;
        Header.MouseLeftButtonUp += Header_Up;
        Look.PositionPicked += p => SetPosition(p, animate: true);
        PositionChanged += () => Look.Refresh();
    }

    // ---------- position ----------

    public void SetPosition(string key, bool animate)
    {
        if (Positions.All(p => p.Key != key)) return;
        Settings.Current.Position = key;
        Settings.Current.Save();
        Place(animate);
        UpdateIdleFace();
        Morph(state); // re-shape for the new spot (upright on the sides)
        PositionChanged?.Invoke();
    }

    /// <summary>
    /// Puts the window in its spot on the main screen (above the taskbar for bottom spots) and
    /// anchors the island so it grows down from the top, or up from the bottom.
    /// </summary>
    void Place(bool animate = false)
    {
        var area = SystemParameters.WorkArea;
        double left = Side switch
        {
            HorizontalAlignment.Left => area.Left + SideGap,
            HorizontalAlignment.Right => area.Right - Width - SideGap,
            _ => area.Left + (area.Width - Width) / 2,
        };
        double top = AtBottom ? area.Bottom - Height
                   : AtMiddle ? area.Top + (area.Height - Height) / 2
                   : area.Top;

        // Top spots grow down, bottom spots grow up, side-middle spots grow out from their centre.
        Shell.HorizontalAlignment = Side;
        LayOutCompact(upright: AtMiddle);
        (RimFade.StartPoint, RimFade.EndPoint) = AtBottom ? (new Point(0, 1), new Point(0, 0))
            : AtMiddle && Side == HorizontalAlignment.Left ? (new Point(0, 0), new Point(1, 0))
            : AtMiddle ? (new Point(1, 0), new Point(0, 0))
            : (new Point(0, 0), new Point(0, 1));
        Shell.VerticalAlignment = AtBottom ? VerticalAlignment.Bottom : AtMiddle ? VerticalAlignment.Center : VerticalAlignment.Top;
        Shell.Margin = AtBottom ? new Thickness(0, 0, 0, Gap) : AtMiddle ? new Thickness(0) : new Thickness(0, Gap, 0, 0);
        Full.VerticalAlignment = AtBottom ? VerticalAlignment.Bottom : AtMiddle ? VerticalAlignment.Center : VerticalAlignment.Top;
        Full.Margin = AtBottom ? new Thickness(0, 0, 0, 10) : AtMiddle ? new Thickness(0, 1, 0, 0) : new Thickness(0, 12, 0, 0);

        if (animate)
        {
            Glide(LeftProperty, Left, left);
            Glide(TopProperty, Top, top);
        }
        else
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = left;
            Top = top;
        }
        Apply();
    }

    /// <summary>Arranges the small music/copied content for a wide or an upright island.</summary>
    void LayOutCompact(bool upright)
    {
        if (upright)
        {
            // cover at the top, waveform at the bottom, check mark in the middle
            CompactArtBox.HorizontalAlignment = HorizontalAlignment.Center;
            CompactArtBox.VerticalAlignment = VerticalAlignment.Top;
            CompactArtBox.Margin = new Thickness(0, 3, 0, 0);
            CompactWave.HorizontalAlignment = HorizontalAlignment.Center;
            CompactWave.VerticalAlignment = VerticalAlignment.Bottom;
            CompactWave.Margin = new Thickness(0, 0, 0, 10);
            CopiedIcon.HorizontalAlignment = HorizontalAlignment.Center;
            CopiedIcon.Margin = new Thickness(0);
            CopiedText.Visibility = Visibility.Collapsed;
            // timer: the light drains downwards, minutes in the middle
            TimerGlyph.Visibility = Visibility.Collapsed;
            TimerText.HorizontalAlignment = HorizontalAlignment.Center;
            TimerText.Margin = new Thickness(0);
            TimerText.FontSize = 9.5;
            TimerDrain.RenderTransformOrigin = new Point(0.5, 1);
            TimerEdge.Visibility = Visibility.Collapsed;
        }
        else
        {
            CompactArtBox.HorizontalAlignment = HorizontalAlignment.Left;
            CompactArtBox.VerticalAlignment = VerticalAlignment.Center;
            CompactArtBox.Margin = new Thickness(4, 0, 0, 0);
            CompactWave.HorizontalAlignment = HorizontalAlignment.Right;
            CompactWave.VerticalAlignment = VerticalAlignment.Center;
            CompactWave.Margin = new Thickness(0, 0, 9, 0);
            CopiedIcon.HorizontalAlignment = HorizontalAlignment.Left;
            CopiedIcon.Margin = new Thickness(4, 0, 0, 0);
            CopiedText.Visibility = Visibility.Visible;
            TimerGlyph.Visibility = Visibility.Visible;
            TimerText.HorizontalAlignment = HorizontalAlignment.Right;
            TimerText.Margin = new Thickness(0, 0, 10, 1);
            TimerText.FontSize = 11;
            TimerDrain.RenderTransformOrigin = new Point(0, 0.5);
            TimerEdge.Visibility = Visibility.Visible;
        }
        TimerDrain.HorizontalAlignment = HorizontalAlignment.Stretch;
        TimerEdge.HorizontalAlignment = HorizontalAlignment.Stretch;
        TimerEdge.RenderTransformOrigin = new Point(0, 0.5);
        OnTimerChanged();
    }

    void Glide(DependencyProperty prop, double from, double to)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        };
        a.Completed += (_, _) => { BeginAnimation(prop, null); SetValue(prop, to); };
        BeginAnimation(prop, a);
    }

    /// <summary>
    /// True when the cursor is pressed against the screen edge right next to the island,
    /// so it opens without having to hit the small pill exactly. Top spots use the top edge;
    /// bottom spots rely on hovering the island, since the taskbar sits at the bottom edge.
    /// </summary>
    bool CursorAtEdge()
    {
        if (AtBottom || !Native.GetCursorPos(out var p)) return false;
        var screen = WF.Screen.PrimaryScreen!.Bounds;
        if (AtMiddle)
        {
            // Side spots: push the cursor against the left/right edge beside the island.
            bool atSide = Side == HorizontalAlignment.Left ? p.X <= screen.Left + 1 : p.X >= screen.Right - 2;
            double centreY = (Top + Height / 2) * dpi;
            return atSide && Math.Abs(p.Y - centreY) <= (height.Target / 2 + 40) * dpi;
        }
        double halfW = width.Target / 2;
        double centreDip = Left + Side switch
        {
            HorizontalAlignment.Left => halfW,
            HorizontalAlignment.Right => Width - halfW,
            _ => Width / 2,
        };
        return p.Y <= screen.Top + 1 && Math.Abs(p.X - centreDip * dpi) <= (halfW + 50) * dpi;
    }

    void Header_Down(object sender, MouseButtonEventArgs e)
    {
        if (!IsOpen || !Native.GetCursorPos(out dragStart)) return;
        windowStart = new Point(Left, Top);
        dragging = false;
        Header.CaptureMouse();
    }

    void Header_Move(object sender, MouseEventArgs e)
    {
        if (!Header.IsMouseCaptured || !Native.GetCursorPos(out var p)) return;
        double dx = (p.X - dragStart.X) / dpi, dy = (p.Y - dragStart.Y) / dpi;
        if (!dragging && Math.Abs(dx) + Math.Abs(dy) < 5) return; // ignore tiny wobbles on a click
        dragging = true;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = windowStart.X + dx;
        Top = windowStart.Y + dy;
    }

    void Header_Up(object sender, MouseButtonEventArgs e)
    {
        if (!Header.IsMouseCaptured) return;
        Header.ReleaseMouseCapture();
        if (!dragging) return;
        dragging = false;

        // Snap to whichever of the six spots is nearest to where it was dropped.
        Native.GetCursorPos(out var p);
        var area = SystemParameters.WorkArea;
        double x = p.X / dpi - area.Left, y = p.Y / dpi - area.Top;
        string col = x < area.Width / 3 ? "Left" : x < area.Width * 2 / 3 ? "Center" : "Right";
        string row = y < area.Height / 3 ? "Top" : y < area.Height * 2 / 3 ? "Middle" : "Bottom";
        if (row == "Middle" && col == "Center") row = y < area.Height / 2 ? "Top" : "Bottom"; // no middle-centre spot
        SetPosition(row + col, animate: true);
    }

    Rect islandRect; // where the island is inside the window, in screen-independent units

    /// <summary>
    /// Asks Windows where the cursor really is. While the island grows its clickable shape changes every
    /// frame, and WPF can briefly think the mouse left when it didn't, which made the panel start closing
    /// and reopen right away.
    /// </summary>
    bool CursorOverIsland()
    {
        if (!Native.GetCursorPos(out var p)) return Shell.IsMouseOver;
        var r = islandRect;
        r.Inflate(4, 4);
        r.Offset(Left, Top);
        return r.Contains(new Point(p.X / dpi, p.Y / dpi));
    }

    // Stay open while typing in a box, or when it was opened with the hotkey.
    bool ShouldStayOpen() =>
        Shell.IsMouseOver || CursorOverIsland() || Shelf.DraggingOut || openedByKey || dragging || Header.IsMouseCaptured || recordingKeys ||
        (IsActive && Keyboard.FocusedElement is TextBox);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd)!;
        dpi = source.CompositionTarget.TransformToDevice.M11;

        // Hardware-drawn window with a see-through background, so the island's edges stay smooth
        // and the animation runs on the graphics card.
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int noRound = Native.DWMWCP_DONOTROUND, noBorder = Native.DWMWA_COLOR_NONE;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

        // Tool window: never shows in Alt+Tab.
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
            Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);

        source.AddHook(WndProc);
        RegisterHotkeys();
        Native.AddClipboardFormatListener(hwnd);
        Apply();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        dpi = newDpi.DpiScaleX;
        Apply();
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotkeys();
        Native.RemoveClipboardFormatListener(hwnd);
        base.OnClosed(e);
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && (int)w == HotkeyToggle)
        {
            Toggle(byKey: true);
            handled = true;
        }
        else if (msg == Native.WM_HOTKEY && (int)w == HotkeyGrab)
        {
            _ = StartGrab();
            handled = true;
        }
        else if (msg == Native.WM_HOTKEY && (int)w == HotkeyGrabImage)
        {
            _ = StartGrabImage();
            handled = true;
        }
        else if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            Clips.OnClipboardChanged();
        }
        return IntPtr.Zero;
    }

    // ---------- springs ----------

    /// <summary>Moves the island to a new state: springs the size, swaps the content.</summary>
    void Morph(Island to)
    {
        var from = state;
        state = to;
        var size = SizeOf(to);
        width.Target = size.Width;
        height.Target = size.Height;
        Full.IsHitTestVisible = to == Island.Open;
        AgentNeeds.IsHitTestVisible = to == Island.Agent;
        Rim.SetResourceReference(Border.BorderBrushProperty, to is Island.Alarm or Island.Agent ? "Orange" : "Green");
        if (to != Island.Open) DropZones.Visibility = Visibility.Collapsed;
        PrivacyDots.Visibility = privacyShown && to != Island.Open ? Visibility.Visible : Visibility.Collapsed;
        if (to == Island.Agent) agentPulse.Start();
        SmartMoment.IsHitTestVisible = to == Island.Smart;
        PhoneMoment.IsHitTestVisible = to == Island.Phone;
        PhoneCodeMoment.IsHitTestVisible = to == Island.PhoneCode;
        CallMoment.IsHitTestVisible = to == Island.Call;
        AlarmMoment.IsHitTestVisible = to == Island.Alarm;
        UpdateIdleLook();
        // Closed panel is Hidden (still laid out, so opening never hitches, but invisible to
        // screen readers and clicks); it becomes Visible as it starts to fade in.
        if (to == Island.Open) Full.Visibility = Visibility.Visible;
        else if (from == Island.Open) HideFullWhenFaded();
        SwapContent(LayerOf(from), LayerOf(to));
        StartAnimating();
    }

    void StartAnimating()
    {
        if (animating) return;
        animating = true;
        lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == lastFrame) return; // same frame reported twice
        double dt = lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - lastFrame).TotalSeconds, 1 / 30.0);
        lastFrame = now;

        width.Step(dt);
        height.Step(dt);
        Apply();
        if (FrameLog != null) File.AppendAllText(FrameLog, $"{now.TotalMilliseconds:0}\t{dt * 1000:0.0}\t{width.Value:0}\t{height.Value:0}\n");

        if (width.Settled && height.Settled)
        {
            animating = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    /// <summary>Applies the current spring values to the island, its rounded clip and the clickable area.</summary>
    void Apply()
    {
        double w = Math.Max(1, width.Value), h = Math.Max(1, height.Value);
        double r = Math.Min(Math.Min(w, h) / 2, MaxRadius);
        Shell.Width = w;
        Shell.Height = h;
        Shell.CornerRadius = new CornerRadius(r);
        Rim.CornerRadius = new CornerRadius(r);
        Inner.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);

        if (hwnd == IntPtr.Zero) return;
        double x = Side switch
        {
            HorizontalAlignment.Left => 0,
            HorizontalAlignment.Right => Width - w,
            _ => (Width - w) / 2,
        };
        double y = AtBottom ? Height - Gap - h : AtMiddle ? (Height - h) / 2 : Gap;
        islandRect = new Rect(x, y, w, h);
        if (fullscreenHidden && !IsOpen)
        {
            Native.SetWindowRgn(hwnd, Native.CreateRectRgn(0, 0, 0, 0), true); // invisible and click-through
            return;
        }
        var region = Native.CreateRectRgn(
            (int)Math.Floor(x * dpi), (int)Math.Floor(y * dpi),
            (int)Math.Ceiling((x + w) * dpi), (int)Math.Ceiling((y + h) * dpi));
        Native.SetWindowRgn(hwnd, region, true); // Windows owns the region from here
    }

    // ---------- content ----------

    UIElement? LayerOf(Island s) => s switch
    {
        Island.Idle => ShowsFace ? IdleFace : null,
        Island.Timer => TimerIsland,
        Island.Alarm => AlarmMoment,
        Island.Agent => AgentNeeds,
        Island.AgentDone => AgentDone,
        Island.Music => CompactMusic,
        Island.Song => SongMoment,
        Island.Copied => CopiedMoment,
        Island.Dial => DialMoment,
        Island.Smart => SmartMoment,
        Island.Shot => ShotMoment,
        Island.Phone => PhoneMoment,
        Island.PhoneCode => PhoneCodeMoment,
        Island.Call => CallMoment,
        Island.Open => Full,
        _ => null,
    };

    /// <summary>Old content fades and blurs out; new content blurs into focus as the island settles.</summary>
    void HideFullWhenFaded()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        t.Tick += (_, _) => { t.Stop(); if (!IsOpen) Full.Visibility = Visibility.Hidden; };
        t.Start();
    }

    static void SwapContent(UIElement? from, UIElement? to)
    {
        if (from == to) return;
        if (from != null)
        {
            from.BeginAnimation(OpacityProperty, Ease(0, 110));
            BlurTo(from, 0, 8, 110);
        }
        if (to != null)
        {
            to.BeginAnimation(OpacityProperty, Ease(1, 240, delayMs: 90));
            BlurTo(to, 10, 0, 300, delayMs: 60);
        }
    }

    static DoubleAnimation Ease(double to, int ms, int delayMs = 0) =>
        new(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

    static void BlurTo(UIElement el, double from, double to, int ms, int delayMs = 0)
    {
        var blur = new BlurEffect { Radius = from, RenderingBias = RenderingBias.Performance };
        el.Effect = blur;
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        // Drop the effect when focused again so text stays perfectly sharp.
        if (to == 0) a.Completed += (_, _) => { if (el.Effect == blur) el.Effect = null; };
        blur.BeginAnimation(BlurEffect.RadiusProperty, a);
    }

    // ---------- open / close ----------

    public void Toggle(bool byKey)
    {
        if (IsOpen) Collapse();
        else Expand(byKey);
    }

    void Expand(bool byKey)
    {
        collapseDelay.Stop();
        momentEnd.Stop();
        if (byKey) openedByKey = true;
        // Remember the app you were in, so snippets and clips can be pasted back into it.
        var fg = Native.GetForegroundWindow();
        if (fg != hwnd && fg != IntPtr.Zero) previousForeground = fg;

        if (fullscreenHidden) Shell.BeginAnimation(OpacityProperty, Ease(1, 160));
        if (!IsOpen)
        {
            bool fly = state == Island.Music && currentTab == "music";
            if (fly) FlyCoverIn();
            Morph(Island.Open);
            openedAt = DateTime.Now;
            if (!fly) RippleKnob();
        }

        if (byKey)
        {
            Activate();
            Native.SetForegroundWindow(hwnd);
            Focus();
        }
    }

    void Collapse()
    {
        if (!IsOpen) return;
        collapseDelay.Stop();

        bool wasActive = IsActive;
        var prev = previousForeground;
        openedByKey = false;
        previousForeground = IntPtr.Zero;

        if (alarmPending) { alarmPending = false; Morph(Island.Alarm); FlashGlow(1); }
        else Morph(Resting);
        if (fullscreenHidden) Shell.BeginAnimation(OpacityProperty, Ease(0, 200, delayMs: 120));

        Keyboard.ClearFocus();
        trimSoon.Stop(); trimSoon.Start();
        // Hand focus back to whatever you were using, so a copied clip can be pasted straight away.
        if (wasActive && prev != IntPtr.Zero) Native.SetForegroundWindow(prev);
    }

    // ---------- fullscreen ----------

    void CheckFullscreen()
    {
        bool fullscreen = !ignoreFullscreen && Native.FullscreenAppInFront(hwnd);
        if (fullscreen == fullscreenHidden) return;
        if (fullscreen && IsOpen && openedByKey) return; // you opened it on purpose: wait until you close it

        fullscreenHidden = fullscreen;
        if (fullscreen)
        {
            if (IsOpen) Collapse();
            momentEnd.Stop();
            if (state != Resting) Morph(Resting);
            Shell.BeginAnimation(OpacityProperty, Ease(0, 220));
        }
        else
        {
            Shell.BeginAnimation(OpacityProperty, Ease(1, 320));
        }
        Apply(); // update the clickable area
    }

    // ---------- dials (mouse wheel) ----------

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        wheelRest += e.Delta / 120.0;
        int notches = (int)Math.Truncate(wheelRest);
        if (notches == 0) { e.Handled = true; return; }
        wheelRest -= notches;

        // Scrolled the instant the cursor arrived: it was meant for the closed island, so turn it
        // into a dial instead of opening.
        bool justHoverOpened = IsOpen && !openedByKey && (DateTime.Now - openedAt).TotalMilliseconds < 350;
        if (justHoverOpened) { collapseDelay.Stop(); Morph(Resting); }

        if (IsOpen && Clips.Visibility == Visibility.Visible)
        {
            Clips.Flip(-notches); // wheel down = older clips
            e.Handled = true;
            return;
        }
        if (IsOpen && Music.Visibility != Visibility.Visible) return; // let other knobs scroll normally

        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _ = SeekDial(notches);
        else VolumeDial(notches);
    }

    void VolumeDial(int notches)
    {
        var v = Audio.Nudge(notches * 0.02);
        if (v == null) return;
        int pct = (int)Math.Round(v.Value.Level * 100);
        string glyph = v.Value.Muted ? "\uE74F" : pct < 34 ? "\uE993" : pct < 67 ? "\uE994" : "\uE995";
        ShowDial(glyph, $"{pct}%", pct / 2.0);
    }

    async Task SeekDial(int notches)
    {
        var at = await Music.SeekBy(notches * 5);
        if (at == null) { ShowDial("\uE916", "can't seek", 0); return; }
        var t = at.Value;
        string text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        ShowDial("\uE916", text, t.TotalSeconds / 5);
    }

    /// <summary>On the closed island the island itself becomes the dial; on the open panel a small dial floats at the bottom.</summary>
    void ShowDial(string glyph, string value, double ticks)
    {
        if (IsOpen)
        {
            PanelDial.Show(glyph, value, ticks);
            PanelDialBox.BeginAnimation(OpacityProperty, Ease(1, 120));
            panelDialHide.Stop();
            panelDialHide.Start();
            return;
        }
        Dial.Show(glyph, value, ticks);
        FlashGlow(0.7);
        if (state != Island.Dial) Morph(Island.Dial);
        momentEnd.Stop();
        momentEnd.Interval = TimeSpan.FromMilliseconds(1300);
        momentEnd.Start();
    }

    /// <summary>Clipboard knob asked to paste: hand focus back to your app, then press Ctrl+V there.</summary>
    void PasteToPreviousApp()
    {
        var target = previousForeground;
        Collapse();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (target != IntPtr.Zero) Native.SetForegroundWindow(target);
            Native.SendCtrlV();
        };
        t.Start();
    }

    // ---------- rim light ----------

    void FlashGlow(double strength = 1)
    {
        glowFlash = Math.Max(glowFlash, strength);
        glowTick.Start();
    }

    void UpdateGlow()
    {
        // Subtle: a faint base while music plays, breathing up with the loudness.
        // Rises quickly on a beat, settles slowly, so it feels like light, not a meter.
        double level = musicPlaying ? Math.Sqrt(Audio.Peak()) : 0;

        // Some apps say they're playing while nothing comes out (a muted tab, a video in the Claude app).
        // After a few silent seconds the island rests; the music look comes back with the sound.
        if (musicPlaying)
        {
            if (level > 0.03) lastSound = DateTime.Now;
            bool silent = (DateTime.Now - lastSound).TotalSeconds > 8;
            if (silent != musicSilent)
            {
                musicSilent = silent;
                if (state is Island.Idle or Island.Music) Morph(Resting);
            }
        }

        // The rim only dances with the sound while you can see the music (the moment or the open panel).
        double target = musicPlaying && !musicSilent && (IsOpen || state == Island.Song) ? 0.14 + 0.5 * level : 0;
        glow += (target - glow) * (target > glow ? 0.45 : 0.12);
        glowFlash *= 0.9;
        Rim.Opacity = Math.Clamp(Math.Max(glow, glowFlash), 0, 1);

        // The little wave bars follow the real sound too.
        if (musicPlaying)
        {
            if (state == Island.Music) DriveWave(CompactWave, level);
            if (state == Island.Song) DriveWave(MomentWave, level);
            if (IsOpen) { DriveWave(Music.WavePanel, level); Music.SetLevel(level); }
        }

        if (!musicPlaying && glow < 0.01 && glowFlash < 0.01)
        {
            glowTick.Stop(); // idle: nothing to draw
            glow = glowFlash = 0;
            Rim.Opacity = 0;
        }
    }

    SmartInfo? smartShown;

    /// <summary>Copied something recognisable: show what it is and one quick action.</summary>
    void ShowSmart(SmartInfo info, string? dragFile = null)
    {
        smartShown = info;
        momentDragFile = dragFile;
        SmartIcon.Text = info.Glyph;
        SmartLabel.Text = info.Label;
        SmartButton.Content = info.Primary.Label;
        if (info.Swatch is Color c) SmartBadge.Fill = new SolidColorBrush(c);
        else SmartBadge.SetResourceReference(Shape.FillProperty, "Blue");
        SmartIcon.Visibility = info.Swatch != null ? Visibility.Collapsed : Visibility.Visible;
        ShowMoment(Island.Smart, 3500);
    }

    void SmartButton_Click(object sender, RoutedEventArgs e)
    {
        if (smartShown == null) return;
        if (smartShown.Kind == SmartKind.Update) { _ = InstallUpdate(); return; }
        if (smartShown.Kind == SmartKind.WhatsNew) { Expand(byKey: false); ShowKnob("settings"); Look.ShowWhatsNew(ThisVersion); return; }
        var action = smartShown.Primary;
        Clips.Run(action);
        if (action.CopyText != null) ShowMoment(Island.Copied, 1200); // e.g. cleaned link is now on the clipboard
        else Morph(Resting);
    }

    DateTime lastMomentAt;

    void ShowMoment(Island moment, int ms)
    {
        if (IsOpen || fullscreenHidden) return;
        bool repeat = state == moment && (DateTime.Now - lastMomentAt).TotalMilliseconds < 600;
        lastMomentAt = DateTime.Now;
        Morph(moment);
        if (!repeat) FlashGlow();
        momentEnd.Stop();
        momentEnd.Interval = TimeSpan.FromMilliseconds(ms);
        momentEnd.Start();
    }

    // ---------- music ----------

    void OnNowPlaying(bool playing, ImageSource? cover)
    {
        Theme.SetCover(cover);
        musicPlaying = playing;
        if (playing) { lastSound = DateTime.Now; musicSilent = false; glowTick.Start(); }
        CompactArt.ImageSource = cover;
        MomentArt.ImageSource = cover;
        Animate(CompactWave, playing);
        Animate(MomentWave, playing);
        Animate(Music.WavePanel, playing);
        if (state is Island.Idle or Island.Music) Morph(Resting);
    }

    void OnTrackChanged(string title, string artist, ImageSource? cover)
    {
        Theme.SetCover(cover);
        MomentTitle.Text = title;
        MomentArtist.Text = artist;
        MomentArt.ImageSource = cover;
        CompactArt.ImageSource = cover;
        ShowMoment(Island.Song, 3200);
    }

    static void BuildWave(Panel panel, int bars, double tall)
    {
        for (int i = 0; i < bars; i++)
        {
            var bar = new Rectangle
            {
                Width = 2, Height = tall, Margin = new Thickness(1, 0, 1, 0),
                RadiusX = 1, RadiusY = 1,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 0.3),
            };
            bar.SetResourceReference(Shape.FillProperty, "Green"); // follows the light colour live
            panel.Children.Add(bar);
        }
    }

    /// <summary>Playing: bars are driven by the live sound (see DriveWave). Stopped: they settle low.</summary>
    static void Animate(Panel wave, bool playing)
    {
        foreach (Rectangle bar in wave.Children)
        {
            var scale = (ScaleTransform)bar.RenderTransform;
            if (playing) { scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); continue; }
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, TimeSpan.FromMilliseconds(200)));
        }
    }

    static readonly double[] WaveSpeeds = { 2.6, 3.4, 2.2, 3.0, 2.8 };

    /// <summary>Each bar rides the sound level with its own little wobble, so they don't move as one block.</summary>
    static void DriveWave(Panel wave, double level)
    {
        double t = Environment.TickCount64 / 1000.0;
        int i = 0;
        foreach (Rectangle bar in wave.Children)
        {
            var scale = (ScaleTransform)bar.RenderTransform;
            double wobble = 0.55 + 0.45 * Math.Sin(t * WaveSpeeds[i % WaveSpeeds.Length] * Math.PI + i * 1.7);
            double target = 0.2 + 0.8 * Math.Min(1, level * 1.3) * wobble;
            scale.ScaleY += (target - scale.ScaleY) * (target > scale.ScaleY ? 0.6 : 0.25);
            i++;
        }
    }

    // ---------- knobs ----------

    static readonly string[] TabOrder = { "music", "claude", "phone", "clips", "timer", "notes", "snippets", "shelf", "convert", "settings" };

    UIElement KnobFor(string tag) => tag switch
    {
        "claude" => Claude, "phone" => PhoneK, "clips" => Clips, "timer" => Timer, "notes" => Notes, "snippets" => Snippets, "shelf" => Shelf,
        "convert" => Converter, "settings" => Look, _ => Music,
    };

    RadioButton TabFor(string tag) => tag switch
    {
        "claude" => TabClaude, "phone" => TabPhone, "clips" => TabClips, "timer" => TabTimer, "notes" => TabNotes, "snippets" => TabSnippets, "shelf" => TabShelf,
        "convert" => TabConvert, "settings" => TabSettings, _ => TabMusic,
    };

    static string ColourKeyOf(string tag) => tag switch
    {
        "clips" => "Blue", "snippets" => "Blue", "timer" => "Orange", "claude" => "Orange", "convert" => "Orange", "settings" => "Fg", _ => "Green",
    };

    static Color TabColour(string tag) => ColourKeyOf(tag) == "Fg" ? Colors.White : Theme.Current(ColourKeyOf(tag));

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (Music == null) return; // still loading
        var tag = (string)((FrameworkElement)sender).Tag;
        if (tag == currentTab) return;
        int dir = Math.Sign(Array.IndexOf(TabOrder, tag) - Array.IndexOf(TabOrder, currentTab));
        var old = KnobFor(currentTab);
        var show = KnobFor(tag);
        currentTab = tag;

        foreach (UIElement knob in Knobs.Children)
            if (knob != show && knob != old) knob.Visibility = Visibility.Collapsed;
        ColourTabs();
        MoveTabLight(animate: true);

        // The old knob slides away in the direction you're moving, the new one springs in from the other side.
        var outShift = new TranslateTransform();
        old.RenderTransform = outShift;
        outShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-dir * 26, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            if (KnobFor(currentTab) != old) old.Visibility = Visibility.Collapsed;
            old.BeginAnimation(OpacityProperty, null);
            old.Opacity = 1;
            old.RenderTransform = null;
        };
        old.BeginAnimation(OpacityProperty, fade);

        show.Visibility = Visibility.Visible;
        var inShift = new TranslateTransform(dir * 34, 0);
        show.RenderTransform = inShift;
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        inShift.BeginAnimation(TranslateTransform.XProperty, Motion.Spring(0, 440, 0.32));
        RippleKnob();
    }

    /// <summary>Rows of the current knob drop in one after another.</summary>
    void RippleKnob()
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (currentTab)
            {
                case "clips": Motion.Ripple(Clips.RippleItems); break;
                case "claude": Motion.Ripple(Claude.RippleItems); break;
                case "phone": Motion.Ripple(PhoneK.RippleItems); break;
                case "notes": if (!Notes.Editing) Motion.Ripple(Notes.RippleItems); break;
                case "snippets": if (!Snippets.Editing) Motion.Ripple(Snippets.RippleItems); break;
                case "shelf": Motion.Ripple(Shelf.RippleItems); break;
                case "settings": Motion.Ripple(Look.RippleItems, max: 14, stepMs: 18); break;
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The little bar of light under the tabs glides to the chosen one.</summary>
    void MoveTabLight(bool animate)
    {
        var tab = TabFor(currentTab);
        if (!tab.IsLoaded || tab.ActualWidth == 0) return;
        double x = tab.TranslatePoint(new Point(tab.ActualWidth / 2 - TabLight.Width / 2, 0), TabBar).X;
        TabLight.SetResourceReference(Border.BackgroundProperty, ColourKeyOf(currentTab));
        TabLightGlow.Color = TabColour(currentTab);
        if (!animate) { TabLightShift.BeginAnimation(TranslateTransform.XProperty, null); TabLightShift.X = x; return; }
        TabLightShift.BeginAnimation(TranslateTransform.XProperty, Motion.Spring(x, 420, 0.45));
        // stretches while it travels, like a drop of light
        var stretch = new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new EasingDoubleKeyFrame(22, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))),
                new EasingDoubleKeyFrame(12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)), new CubicEase { EasingMode = EasingMode.EaseOut }),
            },
        };
        TabLight.BeginAnimation(WidthProperty, stretch);
    }

    /// <summary>iOS-style colour per knob on the selected tab.</summary>
    void ColourTabs()
    {
        if (TabMusic == null) return;
        foreach (var tag in TabOrder)
        {
            var tab = TabFor(tag);
            if (tab.IsChecked == true)
            {
                if (ColourKeyOf(tag) == "Fg") tab.Background = Brushes.White; else tab.SetResourceReference(BackgroundProperty, ColourKeyOf(tag));
                tab.Foreground = Brushes.Black;
            }
            else
            {
                tab.Background = Brushes.Transparent;
                tab.SetResourceReference(ForegroundProperty, "Muted");
            }
        }
    }

    void ShowKnob(string tag) => TabFor(tag).IsChecked = true;

    /// <summary>Opening from the playing island: its tiny cover grows into the big one in the panel.</summary>
    void FlyCoverIn()
    {
        if (AtMiddle || !Music.IsPlayerShown || CompactArtBox.ActualWidth == 0) return;
        var cover = Music.CoverElement;
        try
        {
            var from = CompactArtBox.TranslatePoint(new Point(CompactArtBox.ActualWidth / 2, CompactArtBox.ActualHeight / 2), Full);
            var to = cover.TranslatePoint(new Point(cover.ActualWidth / 2, cover.ActualHeight / 2), Full);
            double s0 = CompactArtBox.ActualWidth / cover.ActualWidth;
            var scale = new ScaleTransform(s0, s0);
            var move = new TranslateTransform(from.X - to.X, from.Y - to.Y);
            cover.RenderTransformOrigin = new Point(0.5, 0.5);
            cover.RenderTransform = new TransformGroup { Children = { scale, move } };
            var grow = Motion.Spring(1, 520, 0.22);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            move.BeginAnimation(TranslateTransform.XProperty, Motion.Spring(0, 520, 0.18));
            move.BeginAnimation(TranslateTransform.YProperty, Motion.Spring(0, 520, 0.18));
        }
        catch (InvalidOperationException) { /* not laid out yet: just open normally */ }
    }

    // ---------- resting face, island style, breathing ----------

    bool ShowsFace => Settings.Current.IslandStyle is "Capsule" or "Outline" && (Settings.Current.IdleClock || (pinnedNote != null && !AtMiddle));

    void UpdateIdleFace()
    {
        pinnedNote = NoteStore.PinnedNote;
        bool note = pinnedNote != null && !AtMiddle;
        IdleNote.Visibility = note ? Visibility.Visible : Visibility.Collapsed;
        IdleClock.Visibility = !note && Settings.Current.IdleClock ? Visibility.Visible : Visibility.Collapsed;
        if (note)
        {
            IdleNoteText.Text = pinnedNote!.Title;
            noteWidth = Math.Clamp(44 + pinnedNote.Title.Length * 5.8, 120, 240);
        }
        UpdateClock();
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        bool h24 = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
        string text = AtMiddle
            ? $"{(h24 ? now.ToString("HH") : now.ToString("%h"))}\n{now:mm}"
            : now.ToString(h24 ? "HH:mm" : "h:mm");
        if (IdleClock.Text != text) IdleClock.Text = text;
    }

    /// <summary>Thin line and Dot are lit from inside; Outline keeps a lit rim; breathing glows slowly in and out.</summary>
    string idleLookKey = "";

    void UpdateIdleLook()
    {
        string style = Settings.Current.IslandStyle;
        bool idle = state == Island.Idle;
        bool breathe = Settings.Current.Breathing;
        // Nothing changed (e.g. music apps saying "still nothing playing" again): don't restart the light,
        // or it visibly dips and comes back.
        string key = $"{idle}|{style}|{breathe}|{ShowsFace}|{privacyShown}";
        if (key == idleLookKey) return;
        idleLookKey = key;

        bool lit = idle && style is "Line" or "Dot" && !privacyShown;
        // switching style while resting: show or hide the time / note straight away
        if (idle && IdleFace != null) IdleFace.BeginAnimation(OpacityProperty, Ease(ShowsFace ? 1 : 0, 200));

        // The Line / Dot light fills the whole island, so it must be off while the island grows or shrinks:
        // off at once when leaving rest, back on only once it has shrunk to the line or dot.
        const int settle = 300;
        if (lit && breathe) IdleFill.BeginAnimation(OpacityProperty, Breathing(0.55, 1, settle));
        else if (lit) IdleFill.BeginAnimation(OpacityProperty, Ease(1, 220, delayMs: settle));
        else { IdleFill.BeginAnimation(OpacityProperty, null); IdleFill.Opacity = 0; }

        bool rim = idle && style is "Capsule" or "Outline";
        if (rim && breathe)
            Breath.BeginAnimation(OpacityProperty, style == "Outline" ? Breathing(0.35, 0.9, settle) : Breathing(0.04, 0.4, settle));
        else if (rim && style == "Outline")
            Breath.BeginAnimation(OpacityProperty, Ease(0.7, 220, delayMs: settle));
        else { Breath.BeginAnimation(OpacityProperty, null); Breath.Opacity = 0; }
    }

    static DoubleAnimation Breathing(double low, double high, int delayMs = 0)
    {
        var a = new DoubleAnimation(low, high, TimeSpan.FromSeconds(2.6))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(a, 24); // slow light: no need to redraw at full speed
        return a;
    }

    // ---------- focus timer ----------

    void OnTimerChanged()
    {
        if (TimerText == null) return;
        var left = FocusTimer.Left;
        TimerText.Text = AtMiddle ? Math.Ceiling(left.TotalMinutes).ToString() : FocusTimer.Format(left);
        TimerText.Opacity = FocusTimer.Paused ? 0.55 : 1;
        double f = FocusTimer.Fraction;
        var drain = AtMiddle ? new ScaleTransform(1, f) : new ScaleTransform(f, 1);
        TimerDrain.RenderTransform = drain;
        TimerEdge.RenderTransform = new ScaleTransform(f, 1);

        if (FocusTimer.Active && state is Island.Idle or Island.Music) Morph(Resting);
        else if (!FocusTimer.Active && state == Island.Timer) Morph(Resting);
    }

    void OnTimerFinished()
    {
        alarmAt = DateTime.Now;
        Chime.Play();
        alarmPulse.Start();
        FlashGlow(1);
        if (IsOpen) { alarmPending = true; return; } // you're using the panel: ring when it closes
        momentEnd.Stop();
        Morph(Island.Alarm);
    }

    void DismissAlarm()
    {
        alarmPulse.Stop();
        alarmPending = false;
        if (state == Island.Alarm) Morph(Resting);
    }

    void AlarmDone_Click(object sender, RoutedEventArgs e) => DismissAlarm();

    // ---------- copied moments ----------

    void ShowCopied(string label = "Copied", int ms = 1400) => ShowInfo("\uE73E", "Blue", label, ms);

    /// <summary>A small one-line moment: a coloured badge with an icon, and a few words.</summary>
    void ShowInfo(string glyph, string colour, string label, int ms)
    {
        CopiedGlyph.Text = glyph;
        CopiedFill.SetResourceReference(Shape.FillProperty, colour);
        CopiedText.Text = label;
        copiedWidth = Math.Max(130, 60 + label.Length * 7.4);
        ShowMoment(Island.Copied, ms);
    }

    // ---------- testing (see TestDriver; only active when LIGHT_TEST is set) ----------

    internal string TestCommand(string cmd, string arg, Func<string, string> snap)
    {
        switch (cmd)
        {
            case "open": Expand(byKey: false); return "open";
            case "close": Collapse(); return "closed";
            case "tab": ShowKnob(arg); return currentTab;
            case "style": Settings.Current.IslandStyle = arg; UpdateIdleFace(); Morph(state); return arg;
            case "clock": Settings.Current.IdleClock = arg == "on"; UpdateIdleFace(); Morph(state); return arg;
            case "breath": Settings.Current.Breathing = arg == "on"; UpdateIdleLook(); return arg;
            case "timer": FocusTimer.StartForTest(TimeSpan.FromSeconds(double.Parse(arg))); return "started";
            case "timerreset": FocusTimer.Reset(); return "reset";
            case "copied": ShowCopied(arg); return "shown";
            case "pos": SetPosition(arg, animate: false); return arg;
            case "note":
                foreach (var n in NoteStore.All) n.Pinned = false;
                var note = new Note { Text = arg, Pinned = true };
                NoteStore.All.Insert(0, note);
                UpdateIdleFace(); Morph(state); return "pinned";
            case "unnote":
                foreach (var n in NoteStore.All.Where(n => n.Pinned).ToList()) NoteStore.All.Remove(n);
                NoteStore.Save(); UpdateIdleFace(); Morph(state); return "removed";
            case "ocr":
                var outFile = System.IO.Path.Combine(Environment.GetEnvironmentVariable("LIGHT_TEST")!, "ocr.txt");
                _ = GrabText.ReadFile(arg).ContinueWith(t => File.WriteAllText(outFile, t.IsFaulted ? "error: " + t.Exception!.InnerException!.Message : t.Result));
                return "reading";
            case "lab": // lab fmt strip|keep maxWidth path1;path2
                var a = arg.Split(' ', 4);
                var labOut = System.IO.Path.Combine(Environment.GetEnvironmentVariable("LIGHT_TEST")!, "lab.txt");
                _ = ConvertKnob.TestConvert(a[3].Split(';'), a[0], 80, int.Parse(a[2]), a[1] == "strip")
                    .ContinueWith(t => File.WriteAllText(labOut, t.IsFaulted ? "error: " + t.Exception!.InnerException!.Message : t.Result));
                return "converting";
            case "key": // key open|grab Ctrl+Alt+L
                var k = arg.Split(' ', 2);
                return TryShortcut(k[0], Hotkey.Parse(k[1])!.Value) ? "ok " + Settings.Current.HotkeyOpen + " / " + Settings.Current.HotkeyGrab : "taken";
            case "agent": // agent needs|done project
                var ag = arg.Split(' ', 2);
                OnAgent(new AgentEvent(ag[0], "test-" + (ag.Length > 1 ? ag[1] : ""), ag.Length > 1 ? ag[1] : "", "", IntPtr.Zero));
                return state.ToString();
            case "lastagent": return Agent.LastReceived?.ToString() ?? "none";
            case "agentclear": agentNeeds.Clear(); Morph(Resting); return "cleared";
            case "feature": // feature SettingName on|off
                var fa = arg.Split(' ');
                typeof(Settings).GetProperty(fa[0])!.SetValue(Settings.Current, fa[1] == "on");
                Look.Refresh(); ApplyFeatures(); return fa[0] + "=" + fa[1];
            case "shelf": ShelfStore.Add(arg.Split(';')); return ShelfStore.All.Count.ToString();
            case "shelfclear": ShelfStore.All.Clear(); ShelfStore.Save(); return "0";
            case "zones": ShowDropZones(arg.Split(';')); HighlightZone(DropShelf, true); return "zones";
            case "hookadd": return Agent.AddHook() ?? "added " + Agent.HookInstalled;
            case "hookremove": return Agent.RemoveHook() ?? "removed " + Agent.HookInstalled;
            case "snippetbody": return SnippetsKnob.Body(SnippetStore.All[int.Parse(arg)].Text).Replace("\n", "|");
            case "nomusic": musicPlaying = false; Animate(CompactWave, false); Morph(Resting); return "idle";
            case "fs": ignoreFullscreen = arg == "off"; CheckFullscreen(); return arg;
            case "state": return $"{state} {width.Value:0}x{height.Value:0} tab={currentTab} hotkeys={HotkeyOk}/{GrabHotkeyOk}/{GrabImageHotkeyOk}";
            case "shot": // shot <png path>: as if a screenshot was just copied
                ShowShot(new ClipItem { Kind = "image", ImageFile = arg, Time = DateTime.Now }); return state.ToString();
            case "info": // info glyphhex colour text
                var ia = arg.Split(' ', 3);
                ShowInfo(((char)Convert.ToInt32(ia[0], 16)).ToString(), ia[1], ia[2], 60000); return state.ToString();
            case "download": ShowDownload(arg); return state.ToString();
            case "whatsnew": Look.ShowWhatsNew(arg); ShowSmart(new SmartInfo(SmartKind.WhatsNew, $"Updated to {arg}", "\uE789", new[] { new SmartAction("What's new") })); return state.ToString();
            case "update": ShowSmart(new SmartInfo(SmartKind.Update, $".light {arg} is out", "", new[] { new SmartAction("Update") })); return state.ToString();
            case "privacy": // privacy cam|mic|both|off  (draws the dots without reading Windows)
                bool c = arg is "cam" or "both", m = arg is "mic" or "both";
                CamDot.Visibility = c ? Visibility.Visible : Visibility.Collapsed;
                MicDot.Visibility = m ? Visibility.Visible : Visibility.Collapsed;
                privacyWatch.Stop();
                HeadCam.Visibility = CamDot.Visibility; HeadMic.Visibility = MicDot.Visibility;
                PrivacyHead.ToolTip = string.Join(Environment.NewLine, new[] { c ? "Camera · Zoom" : null, m ? "Mic · Discord" : null }.Where(x => x != null));
                PrivacyHead.Visibility = c || m ? Visibility.Visible : Visibility.Collapsed;
                privacyShown = c || m;
                PrivacyDots.Visibility = privacyShown && !IsOpen ? Visibility.Visible : Visibility.Collapsed;
                Layers.Margin = new Thickness(0, 0, privacyShown ? 16 : 0, 0);
                UpdateIdleLook(); Morph(state); return arg;
            case "phone": // phone App|Title|Body|call
                var pa = arg.Split('|');
                var pn = new PhoneNote(pa[0], pa[1], pa.Length > 2 ? pa[2] : "", pa.Length > 3 && pa[3] == "call",
                    PhoneNotes.FindCode(pa[1] + " " + (pa.Length > 2 ? pa[2] : "")), DateTime.Now);
                PhoneInbox.Add(pn); ShowPhone(pn); return state + " code=" + (pn.Code ?? "none");
            case "phonexml": // phonexml <file with a toast payload>
                return PhoneNotes.Parse(File.ReadAllText(arg))?.ToString() ?? "null";
            case "phonelink": // phonelink Name|battery
                var pl = arg.Split('|');
                OnPhone(new PhoneLink(pl[0], pl.Length > 1 ? int.Parse(pl[1]) : null)); return state.ToString();
            case "clipon": Settings.Current.PhoneClipCode = arg; return phoneClip.Start() ?? "listening";
            case "phonesetup": Settings.Current.PhoneClipCode ??= PhoneClipListener.NewCode(); ShowKnob("phone"); PhoneK.ShowSetup(); return "setup";
            case "snap": return snap(arg);
            default: return "unknown";
        }
    }

    // ---------- grab text ----------

    public void GrabFromTray() => _ = StartGrab();

    async Task StartGrab()
    {
        if (IsOpen) Collapse();
        await Task.Delay(180); // let the island settle so it isn't in the way
        var text = await GrabText.Run();
        if (text == null) return; // cancelled
        if (text.Length == 0)
        {
            ShowCopied(GrabText.Available ? "No text found" : "Text reading not installed", 2200);
            return;
        }
        pendingCopiedLabel = "Text copied";
        try { Clipboard.SetText(text); }
        catch { pendingCopiedLabel = null; ShowCopied("Clipboard busy, try again", 2200); }
    }

    // ---------- shortcuts ----------

    void RegisterHotkeys()
    {
        if (hwnd == IntPtr.Zero) return;
        HotkeyOk = Register(HotkeyToggle, Hotkey.Parse(Settings.Current.HotkeyOpen) ?? Hotkey.Parse("Alt+Space")!.Value);
        Native.UnregisterHotKey(hwnd, HotkeyGrab);
        Native.UnregisterHotKey(hwnd, HotkeyGrabImage);
        GrabHotkeyOk = !Settings.Current.GrabTextOn ||
            Register(HotkeyGrab, Hotkey.Parse(Settings.Current.HotkeyGrab) ?? Hotkey.Parse("Ctrl+Alt+T")!.Value);
        GrabImageHotkeyOk = !Settings.Current.GrabImageOn ||
            Register(HotkeyGrabImage, Hotkey.Parse(Settings.Current.HotkeyGrabImage) ?? Hotkey.Parse("Ctrl+Alt+S")!.Value);
    }

    void UnregisterHotkeys()
    {
        Native.UnregisterHotKey(hwnd, HotkeyToggle);
        Native.UnregisterHotKey(hwnd, HotkeyGrab);
        Native.UnregisterHotKey(hwnd, HotkeyGrabImage);
    }

    bool Register(int id, Hotkey h)
    {
        Native.UnregisterHotKey(hwnd, id);
        return Native.RegisterHotKey(hwnd, id, h.NativeMods, h.NativeKey);
    }

    /// <summary>Try a new shortcut; keeps the old one if Windows says another app already uses it.</summary>
    bool TryShortcut(string which, Hotkey h)
    {
        int id = which switch { "grab" => HotkeyGrab, "image" => HotkeyGrabImage, _ => HotkeyToggle };
        if (Register(id, h))
        {
            if (which == "grab") { Settings.Current.HotkeyGrab = h.ToString(); GrabHotkeyOk = true; }
            else if (which == "image") { Settings.Current.HotkeyGrabImage = h.ToString(); GrabImageHotkeyOk = true; }
            else { Settings.Current.HotkeyOpen = h.ToString(); HotkeyOk = true; }
            Settings.Current.Save();
            return true;
        }
        RegisterHotkeys(); // put the old one back
        return false;
    }

    // ---------- drag files onto the island: shelf or File lab ----------

    readonly DispatcherTimer dropZonesHide = new() { Interval = TimeSpan.FromMilliseconds(180) };

    static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();

    static bool BothDropZones => Settings.Current.ShowShelf && Settings.Current.ShowFileLab;

    void OnDragEnter(object sender, DragEventArgs e)
    {
        collapseDelay.Stop();
        dropZonesHide.Stop();
        if (OwnDrag || DroppedFiles(e).Length == 0) return; // our own drag out of the shelf, clipboard or island
        if (!Settings.Current.ShowShelf && !Settings.Current.ShowFileLab) return;
        Expand(byKey: false);
        if (BothDropZones) ShowDropZones(DroppedFiles(e));
        else ShowKnob(Settings.Current.ShowShelf ? "shelf" : "convert");
    }

    void ShowDropZones(string[] files)
    {
        bool anyImage = files.Any(f => Directory.Exists(f) || ConvertKnob.IsImage(f));
        DropLab.Opacity = anyImage ? 1 : 0.4;
        DropLabHint.Text = anyImage ? "Convert images" : "Images only";
        HighlightZone(DropShelf, false);
        HighlightZone(DropLab, false);
        DropZones.Visibility = Visibility.Visible;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        collapseDelay.Stop();
        dropZonesHide.Stop();
        bool files = !OwnDrag && e.Data.GetDataPresent(DataFormats.FileDrop)
                     && (Settings.Current.ShowShelf || Settings.Current.ShowFileLab);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!files || !BothDropZones || !IsOpen) return;
        if (DropZones.Visibility != Visibility.Visible) ShowDropZones(DroppedFiles(e));
        bool left = e.GetPosition(DropZones).X < DropZones.ActualWidth / 2;
        HighlightZone(DropShelf, left);
        HighlightZone(DropLab, !left && DropLab.Opacity == 1);
    }

    void HighlightZone(Border zone, bool on)
    {
        if (on) zone.SetResourceReference(Border.BorderBrushProperty, zone == DropShelf ? "Green" : "Orange");
        else zone.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
        zone.Background = new SolidColorBrush(Color.FromArgb(on ? (byte)0x1C : (byte)0x0D, 255, 255, 255));
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        bool zones = DropZones.Visibility == Visibility.Visible;
        var at = e.GetPosition(DropZones);
        DropZones.Visibility = Visibility.Collapsed;
        if (files.Length == 0 || OwnDrag) return;

        bool toLab = !Settings.Current.ShowShelf ||
                     (zones && at.X >= DropZones.ActualWidth / 2 && DropLab.Opacity == 1);
        if (toLab && Settings.Current.ShowFileLab)
        {
            var folders = files.Where(Directory.Exists).ToList();
            Converter.AddFiles(files.Concat(folders.SelectMany(f => Directory.EnumerateFiles(f))));
            ShowKnob("convert");
        }
        else if (Settings.Current.ShowShelf)
        {
            ShelfStore.Add(files);
            ShowKnob("shelf");
        }
    }

    // ---------- features on/off ----------

    void ApplyFeatures()
    {
        var on = new Dictionary<string, bool>
        {
            ["timer"] = Settings.Current.ShowTimer, ["notes"] = Settings.Current.ShowNotes,
            ["snippets"] = Settings.Current.ShowSnippets, ["shelf"] = Settings.Current.ShowShelf,
            ["convert"] = Settings.Current.ShowFileLab, ["claude"] = Settings.Current.AgentStatus, ["phone"] = Settings.Current.PhoneOn,
        };
        foreach (var (tag, visible) in on)
            TabFor(tag).Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (on.TryGetValue(currentTab, out bool shown) && !shown) ShowKnob("music");
        if (!Settings.Current.ShowTimer && FocusTimer.Active) FocusTimer.Reset();
        Clips.ShowGrabButtons(Settings.Current.GrabTextOn, Settings.Current.GrabImageOn);
        if (!Settings.Current.AgentStatus && agentNeeds.Count > 0) { agentNeeds.Clear(); if (state == Island.Agent) Morph(Resting); }
        if (!recordingKeys) RegisterHotkeys();
        if (Settings.Current.DownloadMoment && downloads == null && Environment.GetEnvironmentVariable("LIGHT_TEST") == null)
        {
            downloads = new DownloadWatch();
            downloads.Finished += path => Dispatcher.BeginInvoke(() => ShowDownload(path));
        }
        else if (!Settings.Current.DownloadMoment && downloads != null) { downloads.Dispose(); downloads = null; }
        UpdatePrivacy();
        Dispatcher.BeginInvoke(() => MoveTabLight(animate: false), DispatcherPriority.Loaded);
    }

    // ---------- snippets ----------

    void PasteSnippet(string text)
    {
        try { Clipboard.SetText(text); }
        catch { ShowCopied("Clipboard busy, try again", 2200); return; }
        pendingCopiedLabel = "Pasted";
        PasteToPreviousApp();
    }

    // ---------- grab image ----------

    public void GrabImageFromTray() => _ = StartGrabImage();

    async Task StartGrabImage()
    {
        if (IsOpen) Collapse();
        await Task.Delay(180); // let the island settle so it isn't in the way
        var image = await GrabText.RunImage();
        if (image == null) return; // cancelled
        pendingCopiedLabel = "Image copied";
        try { Clipboard.SetImage(image); }
        catch { pendingCopiedLabel = null; ShowCopied("Clipboard busy, try again", 2200); }
    }

    // ---------- Claude Code status ----------

    readonly Dictionary<string, AgentEvent> agentNeeds = new();
    readonly DispatcherTimer agentPulse = new() { Interval = TimeSpan.FromMilliseconds(1600) };

    void OnAgent(AgentEvent ev)
    {
        if (!Settings.Current.AgentStatus) return;
        ClaudeSessions.Apply(ev);
        string key = ev.Session.Length > 0 ? ev.Session : ev.Project;
        if (ev.Kind is "working" or "ended")
        {
            // a new prompt or a closed session means any OK it was waiting for is settled
            if (agentNeeds.Remove(key)) { UpdateAgentText(); if (state == Island.Agent && agentNeeds.Count == 0) Morph(Resting); }
            return;
        }
        // In a terminal, having that window in front means you can see the prompt. The Claude desktop app
        // holds many sessions in one window, so being in it doesn't mean you saw this one: always show.
        bool looking = ev.Window != IntPtr.Zero && Native.GetForegroundWindow() == ev.Window && !IsClaudeApp(ev.Window);

        if (ev.Kind == "done")
        {
            if (!fullscreenHidden) Chime.Done(); // a softer pop; stays quiet over a full-screen game or video
            if (agentNeeds.Remove(key)) { UpdateAgentText(); if (state == Island.Agent) Morph(Resting); }
            if (looking || fullscreenHidden || IsOpen || state is Island.Alarm or Island.Agent) return; // you're already looking, or something more important is up
            AgentDoneText.Text = ev.Project.Length > 0 ? $"Done · {ev.Project}" : "Claude is done";
            agentDoneWidth = Math.Clamp(62 + AgentDoneText.Text.Length * 7.2, 130, 300);
            ShowMoment(Island.AgentDone, 4000);
            return;
        }

        if (looking) return; // the prompt is right in front of you
        agentNeeds.Remove(key);
        agentNeeds[key] = ev; // newest last
        UpdateAgentText();
        if (!fullscreenHidden) Chime.Pop(); // stays quiet over a full-screen game or video
        if (IsOpen || fullscreenHidden || state is Island.Alarm) return; // shows when the panel closes
        momentEnd.Stop();
        Morph(Island.Agent);
        FlashGlow(1);
        agentPulse.Start();
    }

    void UpdateAgentText()
    {
        if (agentNeeds.Count == 0) return;
        var latest = agentNeeds.Values.Last();
        AgentNeedsText.Text = latest.Project.Length > 0 ? $"{latest.Project} needs your OK to continue" : "Claude needs your OK to continue";
        AgentCount.Text = $"{agentNeeds.Count} waiting";
        AgentCountBox.Visibility = agentNeeds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>You switched to the terminal that asked: it's handled.</summary>
    static IntPtr claudeWindow;
    static bool claudeWindowIs;

    /// <summary>True for the Claude desktop app's window (remembered, as this runs twice a second).</summary>
    static bool IsClaudeApp(IntPtr w)
    {
        if (w != claudeWindow) { claudeWindow = w; claudeWindowIs = Native.ProcessNameOf(w).Equals("claude", StringComparison.OrdinalIgnoreCase); }
        return claudeWindowIs;
    }

    void CheckAgentSeen()
    {
        if (agentNeeds.Count == 0) return;
        var fg = Native.GetForegroundWindow();
        var seen = agentNeeds.Where(kv => kv.Value.Window != IntPtr.Zero && kv.Value.Window == fg && !IsClaudeApp(fg)).Select(kv => kv.Key).ToList();
        if (seen.Count == 0) return;
        foreach (var k in seen) { agentNeeds.Remove(k); ClaudeSessions.Seen(k); }
        UpdateAgentText();
        if (state == Island.Agent) Morph(Resting);
    }

    void AgentShow_Click(object sender, RoutedEventArgs e) { ShowAgentWindow(); e.Handled = true; }

    /// <summary>Brings forward the terminal or editor that asked, and clears it.</summary>
    void ShowAgentWindow()
    {
        if (agentNeeds.Count == 0) { Morph(Resting); return; }
        var (key, ev) = agentNeeds.Last();
        agentNeeds.Remove(key);
        ClaudeSessions.Seen(key);
        if (ev.Window != IntPtr.Zero)
        {
            if (Native.IsIconic(ev.Window)) Native.ShowWindow(ev.Window, 9); // restore if minimised
            Native.SetForegroundWindow(ev.Window);
        }
        UpdateAgentText();
        Morph(Resting);
        if (agentNeeds.Count > 0) agentPulse.Start();
    }

    /// <summary>A row in the Claude tab was clicked: bring that session's window forward.</summary>
    void ShowSession(ClaudeSession session)
    {
        if (agentNeeds.Remove(session.Key)) { UpdateAgentText(); ClaudeSessions.Seen(session.Key); }
        if (session.Window == IntPtr.Zero) return;
        Collapse();
        if (Native.IsIconic(session.Window)) Native.ShowWindow(session.Window, 9);
        Native.SetForegroundWindow(session.Window);
    }

    // ---------- screenshot moment, and dragging things off the island ----------

    ClipItem? shotPending, shotClip;
    string? momentDragFile;
    Point pressAt;
    bool draggedOut;

    bool OwnDrag => Shelf.DraggingOut || ClipboardKnob.DraggingOut || islandDragging;
    static bool islandDragging;

    void ShowShot(ClipItem clip)
    {
        shotClip = clip;
        ShotThumb.ImageSource = clip.Thumb;
        if (IsOpen || fullscreenHidden) return;
        ShowMoment(Island.Shot, 5000);
    }

    void OnMomentDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || OwnDrag) return;
        bool shot = state == Island.Shot && shotClip != null;
        bool file = state == Island.Smart && momentDragFile != null && File.Exists(momentDragFile);
        if (!shot && !file) return;
        var d = e.GetPosition(this) - pressAt;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        draggedOut = true;
        momentEnd.Stop();
        if (shot) ClipboardKnob.DragOut(Shell, shotClip!);
        else
        {
            islandDragging = true;
            try { DragDrop.DoDragDrop(Shell, new DataObject(DataFormats.FileDrop, new[] { momentDragFile! }), DragDropEffects.Copy); }
            catch { }
            finally { islandDragging = false; }
        }
        momentDragFile = null;
        Morph(Resting);
    }

    // ---------- camera and microphone in use ----------

    readonly DispatcherTimer privacyWatch = new() { Interval = TimeSpan.FromSeconds(2) };
    bool privacyShown;

    void UpdatePrivacy()
    {
        List<string> cam = new(), mic = new();
        if (Settings.Current.PrivacyDots) (cam, mic) = Privacy.InUse();
        bool any = cam.Count + mic.Count > 0;
        CamDot.Visibility = cam.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MicDot.Visibility = mic.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var parts = new List<string>();
        if (cam.Count > 0) parts.Add("Camera · " + string.Join(", ", cam));
        if (mic.Count > 0) parts.Add("Mic · " + string.Join(", ", mic));
        HeadCam.Visibility = CamDot.Visibility;
        HeadMic.Visibility = MicDot.Visibility;
        PrivacyHead.ToolTip = string.Join(Environment.NewLine, parts);
        PrivacyHead.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        // the dots sit inside the closed island (a resting Line or Dot becomes a small pill to hold them)
        bool show = any && !AtMiddle;
        PrivacyDots.Visibility = show && !IsOpen ? Visibility.Visible : Visibility.Collapsed;
        Layers.Margin = new Thickness(0, 0, show ? 16 : 0, 0);
        if (show != privacyShown)
        {
            privacyShown = show;
            if (show) FlashGlow(0.8);
            UpdateIdleLook();
            Morph(state);
        }
    }

    // ---------- charging and battery ----------

    readonly DispatcherTimer batteryWatch = new() { Interval = TimeSpan.FromSeconds(60) };
    bool lastCharging, lowShown;

    void CheckPower()
    {
        if (!Power.HasBattery) return;
        bool charging = Power.Charging;
        int pct = Power.Percent;
        if (charging && !lastCharging && Settings.Current.PowerMoment)
            ShowInfo("\uE945", "Green", $"Charging · {pct}%", 2600);
        if (charging) lowShown = false;
        else if (pct <= 15 && !lowShown && Settings.Current.PowerMoment)
        {
            lowShown = true;
            ShowInfo("\uE7BA", "Orange", $"Battery low · {pct}%", 5000);
        }
        lastCharging = charging;
    }

    // ---------- finished downloads ----------

    DownloadWatch? downloads;

    void ShowDownload(string path)
    {
        if (!Settings.Current.DownloadMoment) return;
        var name = System.IO.Path.GetFileName(path);
        var info = new SmartInfo(SmartKind.Download, $"Downloaded · {name}", "\uE896", new[] { new SmartAction("Show", Reveal: path) });
        ShowSmart(info, path);
    }

    // ---------- new version ----------

    readonly DispatcherTimer updateWatch = new() { Interval = TimeSpan.FromSeconds(90) };

    async Task CheckUpdate()
    {
        if (!Settings.Current.UpdateNotice || Environment.GetEnvironmentVariable("LIGHT_TEST") != null) return;
        var version = await Updates.Check();
        if (version == null || version == Settings.Current.UpdateShown) return;
        ShowSmart(new SmartInfo(SmartKind.Update, $".light {version} is out", "\uE777", new[] { new SmartAction("Update") }));
        if (state != Island.Smart) return; // couldn't show right now (fullscreen, panel open): try again next time
        Settings.Current.UpdateShown = version;
        Settings.Current.Save();
    }

    // ---------- what's new, once after an update ----------

    readonly DispatcherTimer whatsNewSoon = new() { Interval = TimeSpan.FromSeconds(6) };
    readonly bool ranBefore = Settings.Current.Welcomed; // read before this start marks you as welcomed

    static string ThisVersion => (System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "")
        .Split('+')[0];

    void AnnounceUpdate()
    {
        var now = ThisVersion;
        var before = Settings.Current.LastVersion;
        if (Environment.GetEnvironmentVariable("LIGHT_TEST") != null || now.Length == 0 || before == now) return;
        Settings.Current.LastVersion = now;
        Settings.Current.Save();
        if (before == null && !ranBefore) return; // a fresh install, not an update
        Look.ShowWhatsNew(now); // waiting at the top of Look whenever you open it
        ShowSmart(new SmartInfo(SmartKind.WhatsNew, $"Updated to {now}", "\uE789", new[] { new SmartAction("What's new") }));
    }

    async Task InstallUpdate()
    {
        ShowInfo("\uE896", "Green", "Updating .light…", 30000);
        if (!await Updates.Install()) ShowCopied("Couldn't update, try again later", 2600);
    }

    // ---------- phone (through Phone Link) ----------

    readonly DispatcherTimer phoneWatch = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer phoneStatusWatch = new() { Interval = TimeSpan.FromSeconds(8) };
    readonly PhoneClipListener phoneClip = new();
    PhoneNote? phoneShown;
    string? lastPhoneName;
    bool phoneLowShown, phoneReading;

    void CheckPhoneNotes()
    {
        if (!Settings.Current.PhoneOn || phoneReading || Environment.GetEnvironmentVariable("LIGHT_TEST") != null) return;
        phoneReading = true;
        Task.Run(PhoneNotes.TakeNew).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            phoneReading = false;
            if (t.IsFaulted) return;
            foreach (var n in t.Result) { PhoneInbox.Add(n); ShowPhone(n); }
        }));
    }

    /// <summary>A phone notification on the island: app and sender, the message if you allow it, a code to copy.</summary>
    void ShowPhone(PhoneNote n)
    {
        if (!Settings.Current.PhoneOn) return;
        if (Settings.Current.PhonePop && !fullscreenHidden) Chime.Done();
        if (IsOpen || fullscreenHidden || state is Island.Alarm or Island.Agent) return; // it's in the Phone tab
        phoneShown = n;
        string who = n.App == "Phone" ? n.Title : $"{n.App} · {n.Title}";
        if (n.IsCall)
        {
            CallName.Text = n.Title;
            CallInitial.Text = n.Title.Length > 0 ? n.Title[..1].ToUpperInvariant() : "?";
            ShowMoment(Island.Call, 15000);
            return;
        }
        if (n.Code != null)
        {
            CodeBadgeText.Text = n.App.Length > 0 ? n.App[..1].ToUpperInvariant() : "•";
            CodeTitle.Text = who;
            CodeText.Text = n.Code.Length == 6 ? n.Code[..3] + " " + n.Code[3..] : n.Code;
            ShowMoment(Island.PhoneCode, 12000);
            return;
        }
        PhoneBadge.Fill = Palette.LimeBrush;
        PhoneBadgeText.FontFamily = n.IsCall ? (FontFamily)FindResource("Icons") : new FontFamily("Segoe UI");
        PhoneBadgeText.Text = n.IsCall ? "\uE717" : n.App.Length > 0 ? n.App[..1].ToUpperInvariant() : "\u2022";
        PhoneTitle.Text = n.IsCall ? $"Call · {n.Title}" : n.App == "Phone" ? n.Title : $"{n.App} · {n.Title}";
        PhoneBody.Text = n.IsCall ? "Incoming call"
            : Settings.Current.PhoneShowMessage && n.Body.Length > 0 ? n.Body
            : n.Code != null ? "Has a code" : "New message";
        PhoneButton.Content = n.Code != null ? $"Copy {n.Code}" : n.IsCall ? "Open" : "";
        PhoneButton.Visibility = n.Code != null || n.IsCall ? Visibility.Visible : Visibility.Collapsed;
        ShowMoment(Island.Phone, n.IsCall ? 15000 : 5000);
    }

    void PhoneButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        draggedOut = true; // keep the island's own click from also running
        if (phoneShown?.Code is string code) CopyCode(code);
        else { PhoneInbox.OpenPhoneLink(); Morph(Resting); }
    }

    void CopyCode(string code)
    {
        pendingCopiedLabel = "Code copied";
        try { Clipboard.SetText(code); }
        catch { pendingCopiedLabel = null; ShowCopied("Clipboard busy, try again", 2200); }
    }

    async Task CheckPhone()
    {
        if (!Settings.Current.PhoneOn || Environment.GetEnvironmentVariable("LIGHT_TEST") != null) return;
        OnPhone(await PhoneStatus.Find());
    }

    /// <summary>The phone connected, disconnected, or its battery changed.</summary>
    void OnPhone(PhoneLink? p)
    {
        PhoneInbox.SetPhone(p);
        if (p == null) { lastPhoneName = null; return; }
        if (lastPhoneName != p.Name)
        {
            lastPhoneName = p.Name;
            phoneLowShown = false;
            ShowInfo("\uE8EA", "Green", p.Battery is int b ? $"{p.Name} connected · {b}%" : $"{p.Name} connected", 3000);
            return;
        }
        if (p.Battery is int level && level <= 15 && !phoneLowShown)
        {
            phoneLowShown = true;
            ShowInfo("\uE7BA", "Orange", $"{p.Name} battery low · {level}%", 5000);
        }
        else if (p.Battery is int ok && ok > 20) phoneLowShown = false;
    }

    /// <summary>Text the iPhone Shortcut sent: it goes on the clipboard (and so into the history).</summary>
    string? lastPhoneText;

    void FromPhoneClipboard(string text)
    {
        if (text == lastPhoneText) return; // the same copy sent again (an automation): nothing new
        lastPhoneText = text;
        pendingCopiedLabel = "From iPhone";
        try { Clipboard.SetText(text); }
        catch { pendingCopiedLabel = null; }
    }
}
