using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DotLight.Knobs;

/// <summary>Timer knob: a kitchen-timer dial. Scroll to set minutes, start, and the island counts down.</summary>
public partial class TimerKnob : UserControl
{
    const double Size = 132, Radius = 56, Centre = Size / 2;
    static readonly int[] PresetMinutes = { 5, 15, 25, 45 };
    double shownFraction = -1;
    double wheelRest;

    public TimerKnob()
    {
        InitializeComponent();
        DrawTicks();
        foreach (var m in PresetMinutes)
        {
            var chip = new Button { Style = (Style)FindResource("ChipBtn"), Content = $"{m} min", Tag = m };
            chip.Click += (_, _) => { if (!FocusTimer.Active) FocusTimer.SetMinutes(m); };
            Presets.Children.Add(chip);
        }
        DialFace.MouseWheel += OnWheel;
        FocusTimer.Changed += Show;
        Theme.Changed += () => ArcGlow.Color = Theme.Current("Orange");
        ArcGlow.Color = Theme.Current("Orange");
        Loaded += (_, _) => Show();
    }

    /// <summary>60 little marks around the dial, every fifth one longer.</summary>
    void DrawTicks()
    {
        var faint = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
        var strong = new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255));
        for (int i = 0; i < 60; i++)
        {
            double a = i / 60.0 * 2 * Math.PI - Math.PI / 2;
            bool major = i % 5 == 0;
            double r1 = Radius + 8, r2 = Radius + (major ? 13 : 11);
            Ticks.Children.Add(new Line
            {
                X1 = Centre + Math.Cos(a) * r1, Y1 = Centre + Math.Sin(a) * r1,
                X2 = Centre + Math.Cos(a) * r2, Y2 = Centre + Math.Sin(a) * r2,
                Stroke = major ? strong : faint, StrokeThickness = major ? 1.4 : 1,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        }
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        wheelRest += e.Delta / 120.0;
        int notches = (int)Math.Truncate(wheelRest);
        if (notches == 0) return;
        wheelRest -= notches;
        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 1;
        if (FocusTimer.Active) FocusTimer.Adjust(TimeSpan.FromMinutes(notches * step));
        else FocusTimer.SetMinutes((int)FocusTimer.Total.TotalMinutes + notches * step);
        Pulse();
    }

    /// <summary>A little click of light on every turn.</summary>
    void Pulse()
    {
        var a = new DoubleAnimation(22, 12, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        ArcGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, a);
    }

    void Show()
    {
        BigTime.Text = FocusTimer.Format(FocusTimer.Left);
        Caption.Text = FocusTimer.Running ? "counting down" : FocusTimer.Paused ? "paused" : "scroll to set";
        StartBtn.Content = FocusTimer.Running ? "Pause" : FocusTimer.Paused ? "Resume" : "Start";
        ResetBtn.Visibility = FocusTimer.Active ? Visibility.Visible : Visibility.Collapsed;
        foreach (Button chip in Presets.Children) chip.IsEnabled = !FocusTimer.Active;

        // Setting: the arc shows minutes on a 60-minute face. Running: what's left of the round.
        double f = FocusTimer.Active ? FocusTimer.Fraction : Math.Min(1, FocusTimer.Total.TotalMinutes / 60);
        if (Math.Abs(f - shownFraction) < 0.0005) return;
        shownFraction = f;
        Arc.Data = ArcGeometry(f);
    }

    static Geometry ArcGeometry(double f)
    {
        if (f <= 0.0005) return Geometry.Empty;
        if (f >= 0.9995) return new EllipseGeometry(new Point(Centre, Centre), Radius, Radius);
        double a = f * 2 * Math.PI - Math.PI / 2;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(Centre, Centre - Radius), false, false);
            c.ArcTo(new Point(Centre + Math.Cos(a) * Radius, Centre + Math.Sin(a) * Radius),
                new Size(Radius, Radius), 0, f > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    void Start_Click(object sender, RoutedEventArgs e)
    {
        if (FocusTimer.Running) FocusTimer.Pause();
        else FocusTimer.Start();
    }

    void Reset_Click(object sender, RoutedEventArgs e) => FocusTimer.Reset();
}
