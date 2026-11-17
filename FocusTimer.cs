using System.Windows.Threading;

namespace DotLight;

/// <summary>The countdown behind the Timer knob; the island and the knob both watch it.</summary>
static class FocusTimer
{
    static readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    static DateTime endsAt;
    static TimeSpan pausedLeft;

    public static TimeSpan Total { get; private set; } = TimeSpan.FromMinutes(Settings.Current.TimerMinutes);
    public static bool Running { get; private set; }
    public static bool Paused => !Running && pausedLeft > TimeSpan.Zero;
    public static bool Active => Running || Paused;

    public static TimeSpan Left => Running
        ? (endsAt > DateTime.Now ? endsAt - DateTime.Now : TimeSpan.Zero)
        : Paused ? pausedLeft : Total;

    /// <summary>1 when full, 0 when done.</summary>
    public static double Fraction => Total.TotalSeconds <= 0 ? 0 : Math.Clamp(Left.TotalSeconds / Total.TotalSeconds, 0, 1);

    /// <summary>Raised a few times a second while running, and on every change.</summary>
    public static event Action? Changed;
    public static event Action? Finished;

    static FocusTimer()
    {
        tick.Tick += (_, _) =>
        {
            if (Running && Left <= TimeSpan.Zero)
            {
                Running = false;
                pausedLeft = TimeSpan.Zero;
                tick.Stop();
                Changed?.Invoke();
                Finished?.Invoke();
                return;
            }
            Changed?.Invoke();
        };
    }

    /// <summary>Sets the length before starting (1–180 minutes).</summary>
    public static void SetMinutes(int minutes)
    {
        minutes = Math.Clamp(minutes, 1, 180);
        Total = TimeSpan.FromMinutes(minutes);
        Settings.Current.TimerMinutes = minutes;
        Settings.Current.Save();
        Changed?.Invoke();
    }

    /// <summary>Adds or takes away time while it runs or is paused.</summary>
    public static void Adjust(TimeSpan by)
    {
        if (Running)
        {
            var left = Left + by;
            if (left < TimeSpan.FromSeconds(5)) left = TimeSpan.FromSeconds(5);
            endsAt = DateTime.Now + left;
            if (left > Total) Total = left;
        }
        else if (Paused)
        {
            pausedLeft += by;
            if (pausedLeft < TimeSpan.FromSeconds(5)) pausedLeft = TimeSpan.FromSeconds(5);
            if (pausedLeft > Total) Total = pausedLeft;
        }
        Changed?.Invoke();
    }

    public static void Start()
    {
        if (Running) return;
        var length = Paused ? pausedLeft : Total;
        endsAt = DateTime.Now + length;
        pausedLeft = TimeSpan.Zero;
        Running = true;
        tick.Start();
        Changed?.Invoke();
    }

    /// <summary>Testing: a very short round.</summary>
    internal static void StartForTest(TimeSpan length)
    {
        Total = length;
        pausedLeft = TimeSpan.Zero;
        Running = false;
        endsAt = DateTime.Now + length;
        Running = true;
        tick.Start();
        Changed?.Invoke();
    }

    public static void Pause()
    {
        if (!Running) return;
        pausedLeft = Left;
        Running = false;
        tick.Stop();
        Changed?.Invoke();
    }

    public static void Reset()
    {
        Running = false;
        pausedLeft = TimeSpan.Zero;
        tick.Stop();
        Total = TimeSpan.FromMinutes(Settings.Current.TimerMinutes);
        Changed?.Invoke();
    }

    public static string Format(TimeSpan t)
    {
        int secs = (int)Math.Ceiling(t.TotalSeconds);
        return secs >= 3600 ? $"{secs / 3600}:{secs / 60 % 60:00}:{secs % 60:00}" : $"{secs / 60}:{secs % 60:00}";
    }
}
