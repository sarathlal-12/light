using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DotLight.Knobs;

/// <summary>The tick-ruler dial shown while you turn a knob with the mouse wheel.</summary>
public partial class TickDial : UserControl
{
    const double Spacing = 6, Tile = Spacing * 5; // one long tick, then four short ones
    readonly TranslateTransform shift = new();

    public TickDial()
    {
        InitializeComponent();
        Ruler.Fill = MakeTicks();
    }

    TileBrush MakeTicks()
    {
        var group = new DrawingGroup();
        var strong = new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255));
        var faint = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255));
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, Tile, 14))));
        group.Children.Add(new GeometryDrawing(strong, null, new RectangleGeometry(new Rect(0, 1, 1.5, 12), 0.75, 0.75)));
        for (int i = 1; i < 5; i++)
            group.Children.Add(new GeometryDrawing(faint, null, new RectangleGeometry(new Rect(i * Spacing, 4, 1.2, 6), 0.6, 0.6)));

        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, Tile, 14),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            Transform = shift,
        };
    }

    /// <summary>
    /// Shows a reading. <paramref name="ticks"/> is the dial position in tick steps
    /// (e.g. volume % / 2); the ruler glides there so each wheel notch feels like a click.
    /// </summary>
    public void Show(string glyph, string value, double ticks)
    {
        Glyph.Text = glyph;
        Value.Text = value;
        shift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-ticks * Spacing, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });

        // tiny pulse on the needle for every step
        var pulse = new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var scale = Needle.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        Needle.RenderTransformOrigin = new Point(0.5, 0.5);
        Needle.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }
}
