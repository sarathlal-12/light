using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DotLight.Knobs;

/// <summary>One Claude Code session as .light last heard of it.</summary>
public class ClaudeSession
{
    public string Key = "", Project = "", State = "working"; // working, needs, done
    public DateTime Since = DateTime.Now;      // when it entered this state
    public DateTime? WorkStarted;              // when the current piece of work began
    public TimeSpan? Took;                     // how long the last piece of work took
    public IntPtr Window;
}

/// <summary>Every Claude Code session the hook has told us about, newest first. Lives only in memory.</summary>
static class ClaudeSessions
{
    public static readonly List<ClaudeSession> All = new();
    public static event Action? Changed;

    public static void Apply(AgentEvent ev)
    {
        string key = ev.Session.Length > 0 ? ev.Session : ev.Project;
        var s = All.FirstOrDefault(x => x.Key == key);
        if (ev.Kind == "ended") { if (s != null) All.Remove(s); Changed?.Invoke(); return; }
        if (s == null) { s = new ClaudeSession { Key = key }; All.Insert(0, s); }
        if (ev.Project.Length > 0) s.Project = ev.Project;
        if (ev.Window != IntPtr.Zero) s.Window = ev.Window;
        var now = DateTime.Now;
        switch (ev.Kind)
        {
            case "working":
                s.WorkStarted = now; s.Took = null; break;
            case "needs":
                s.WorkStarted ??= now; break;
            case "done":
                if (s.WorkStarted is DateTime w) s.Took = now - w;
                s.WorkStarted = null; break;
        }
        s.State = ev.Kind;
        s.Since = now;
        // finished sessions fade out of the list after a few hours
        All.RemoveAll(x => x.State == "done" && now - x.Since > TimeSpan.FromHours(3));
        Changed?.Invoke();
    }

    /// <summary>You looked at a session that needed your OK: it carries on working.</summary>
    public static void Seen(string key)
    {
        var s = All.FirstOrDefault(x => x.Key == key);
        if (s == null || s.State != "needs") return;
        s.State = "working"; s.Since = DateTime.Now;
        Changed?.Invoke();
    }

    public static void ClearDone() { All.RemoveAll(x => x.State == "done"); Changed?.Invoke(); }
}

/// <summary>Claude tab: each Claude Code session with what it's doing right now.</summary>
public partial class ClaudeKnob : UserControl
{
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>A row was clicked: bring that session's window forward.</summary>
    public event Action<ClaudeSession>? ShowRequested;

    public ClaudeKnob()
    {
        InitializeComponent();
        ClaudeSessions.Changed += () => { if (IsVisible) Build(); };
        tick.Tick += (_, _) => Build();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Build(); tick.Start(); } else tick.Stop(); };
        Build();
    }

    public IEnumerable<FrameworkElement> RippleItems => Rows.Children.OfType<FrameworkElement>().Take(8);

    void Clear_Click(object sender, RoutedEventArgs e) => ClaudeSessions.ClearDone();

    void Build()
    {
        Rows.Children.Clear();
        var list = ClaudeSessions.All;
        ClearBtn.Visibility = list.Any(s => s.State == "done") ? Visibility.Visible : Visibility.Collapsed;
        int working = list.Count(s => s.State == "working"), needs = list.Count(s => s.State == "needs");
        Subtitle.Text = list.Count == 0 ? "Your sessions, live"
            : needs > 0 ? $"{needs} waiting for your OK" + (working > 0 ? $" · {working} working" : "")
            : working > 0 ? $"{working} working" : "All done";

        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = !Settings.Current.AgentStatus
            ? "Turn on Claude Code status in Look (the gear), then your sessions show up here."
            : "No Claude Code sessions yet. Send Claude a prompt and it shows up here.";

        Rings.Children.Clear();
        foreach (var s in list.Take(3)) Rings.Children.Add(Ring(s));
        Rings.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var s in list.Skip(3)) Rows.Children.Add(Row(s));
    }

    FrameworkElement Row(ClaudeSession s)
    {
        var now = DateTime.Now;
        // fixed colours (not the light colour), so the three states are always told apart
        var colour = s.State switch { "needs" => Needs, "done" => DoneC, _ => WorkingC };
        string glyph = s.State switch { "needs" => "", "done" => "", _ => "" };
        string status = s.State switch
        {
            "needs" => $"Needs your OK · {Ago(now - s.Since)}",
            "done" => s.Took is TimeSpan t ? $"Done {Ago(now - s.Since)} · took {Span(t)}" : $"Done · {Ago(now - s.Since)}",
            _ => $"Working · {Span(now - (s.WorkStarted ?? s.Since))}",
        };

        var badge = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
        var dot = new System.Windows.Shapes.Ellipse();
        dot.Fill = colour;
        badge.Children.Add(dot);
        badge.Children.Add(new TextBlock
        {
            FontFamily = (FontFamily)FindResource("Icons"), Text = glyph, FontSize = 8.5,
            Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = s.Project.Length > 0 ? s.Project : "Claude Code", FontWeight = FontWeights.SemiBold, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var line = new TextBlock { Text = status, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
        if (s.State == "needs") line.Foreground = Needs; else line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(line);

        var dock = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        dock.Children.Add(badge);
        dock.Children.Add(text);

        var row = new Border
        {
            Style = (Style)FindResource("HoverRow"), Child = dock,
            ToolTip = s.Window != IntPtr.Zero ? "Click to bring it forward" : null,
        };
        row.MouseLeftButtonUp += (_, _) => ShowRequested?.Invoke(s);
        return row;
    }

    static readonly Brush WorkingC = Palette.SkyBrush, Needs = Palette.AmberBrush, DoneC = Palette.LimeBrush;
    static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    /// <summary>A session as a ring: blue and filling while it works, amber when it needs you, green when done.</summary>
    FrameworkElement Ring(ClaudeSession s)
    {
        var now = DateTime.Now;
        var colour = s.State switch { "needs" => Needs, "done" => DoneC, _ => WorkingC };
        const double size = 76, thick = 8;
        double fraction = s.State == "working" ? ((now - (s.WorkStarted ?? s.Since)).TotalSeconds % 60) / 60 : 1;
        double circ = Math.PI * (size - thick) / thick; // dash lengths count in stroke widths
        var track = new System.Windows.Shapes.Ellipse { Width = size, Height = size, StrokeThickness = thick, Stroke = Palette.Brush(Color.FromRgb(0x2C, 0x2C, 0x2E)) };
        var arc = new System.Windows.Shapes.Ellipse
        {
            Width = size, Height = size, StrokeThickness = thick, Stroke = colour,
            StrokeDashArray = new DoubleCollection { Math.Max(0.01, circ * fraction), circ * 2 },
            StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-90),
        };
        var centre = new TextBlock
        {
            Text = s.State switch { "needs" => "!", "done" => "✓", _ => Span(now - (s.WorkStarted ?? s.Since)) },
            FontWeight = FontWeights.Bold, FontSize = s.State == "working" ? 14 : 24, Foreground = colour,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var circle = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        circle.Children.Add(track); circle.Children.Add(arc); circle.Children.Add(centre);

        var name = new TextBlock { Text = s.Project.Length > 0 ? s.Project : "Claude Code", FontWeight = FontWeights.SemiBold, FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 7, 0, 0) };
        var status = new TextBlock { Text = s.State switch { "needs" => "Needs your OK", "done" => s.Took is TimeSpan t ? $"Done · {Span(t)}" : "Done", _ => "Working" },
            FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var box = new StackPanel { Width = 130, Margin = new Thickness(0, 0, 18, 0), Cursor = s.Window != IntPtr.Zero ? System.Windows.Input.Cursors.Hand : null,
            Background = Brushes.Transparent, ToolTip = s.Window != IntPtr.Zero ? "Click to bring it forward" : null };
        box.Children.Add(circle); box.Children.Add(name); box.Children.Add(status);
        box.MouseLeftButtonUp += (_, _) => ShowRequested?.Invoke(s);
        return box;
    }

    /// <summary>"12s", "4m 05s", "1h 20m".</summary>
    internal static string Span(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s"
        : $"{Math.Max(0, (int)t.TotalSeconds)}s";

    static string Ago(TimeSpan t) =>
        t.TotalSeconds < 10 ? "just now"
        : t.TotalMinutes < 1 ? $"{(int)t.TotalSeconds}s ago"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m ago"
        : $"{(int)t.TotalHours}h ago";
}
