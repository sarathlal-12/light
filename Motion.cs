using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DotLight;

/// <summary>Small shared animations: rows rippling in, buttons squashing when pressed.</summary>
static class Motion
{
    /// <summary>Rows drop in one after another: fade up from a few pixels below, with a slight overshoot.</summary>
    public static void Ripple(IEnumerable<FrameworkElement> items, int max = 10, int stepMs = 28)
    {
        int i = 0;
        foreach (var el in items)
        {
            if (i >= max) break;
            if (el.Visibility != Visibility.Visible) continue;
            var shift = el.RenderTransform as TranslateTransform;
            if (shift == null)
            {
                if (el.RenderTransform != null && el.RenderTransform != Transform.Identity) continue; // owned by something else
                shift = new TranslateTransform();
                el.RenderTransform = shift;
            }
            var begin = TimeSpan.FromMilliseconds(i * stepMs);
            // sits a little low until its turn comes, then springs up
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimationUsingKeyFrames
            {
                FillBehavior = FillBehavior.Stop,
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(9, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new DiscreteDoubleKeyFrame(9, KeyTime.FromTimeSpan(begin)),
                    new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(begin + TimeSpan.FromMilliseconds(380)),
                        new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }),
                },
            });
            // hidden until its turn comes
            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimationUsingKeyFrames
                {
                    FillBehavior = FillBehavior.Stop,
                    KeyFrames =
                    {
                        new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                        new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(begin)),
                        new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(begin + TimeSpan.FromMilliseconds(220))),
                    },
                });
            i++;
        }
    }

    /// <summary>Every button in the app squashes a little when pressed and springs back on release.</summary>
    public static void EnableButtonSquash()
    {
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((s, _) => Squash((UIElement)s, 0.9, 90, null)));
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((s, _) => Squash((UIElement)s, 1, 420, new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut })));
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseLeaveEvent,
            new MouseEventHandler((s, _) => Squash((UIElement)s, 1, 200, null)));
    }

    static void Squash(UIElement el, double to, int ms, IEasingFunction? ease)
    {
        if (el is not FrameworkElement fe) return;
        if (fe.RenderTransform is not ScaleTransform scale)
        {
            if (fe.RenderTransform != null && fe.RenderTransform != Transform.Identity) return;
            if (to == 1) return; // never squashed
            scale = new ScaleTransform(1, 1);
            fe.RenderTransform = scale;
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    public static DoubleAnimation Spring(double to, int ms, double amplitude = 0.3, int delayMs = 0) =>
        new(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new BackEase { Amplitude = amplitude, EasingMode = EasingMode.EaseOut },
        };
}
