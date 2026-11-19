using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DotLight;

/// <summary>
/// The fun features: the island pet, shy mode, typing fire, confetti, the rage-click detector, the mood
/// ring, touch grass and night owl. All off by default. Lightweight on purpose: a feature that's off runs
/// nothing, the pet, grass and night owl share one 2-second tick, and typing and clicks are read from the
/// 25 ms check the island already does for hovering (only when, never which key).
/// </summary>
public partial class NotchWindow
{
    static readonly bool Testing = Environment.GetEnvironmentVariable("LIGHT_TEST") != null;

    readonly DispatcherTimer funTick = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer moodTick = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Random rng = new();

    // typing fire / pet
    double fire;                    // typing heat, 0..1
    uint lastInputTick;
    Native.POINT lastCursor;
    DateTime lastTypedAt = DateTime.MinValue;

    // mood ring
    SystemLoad? load;
    double mood = -1;               // the busier of CPU and GPU, 0..1 (-1 = off)
    Temperatures? temps;
    double cpuUse = -1;
    double? gpuUse, cpuTemp, gpuTemp;

    // confetti
    RecycleBinWatch? bin;
    readonly List<Bit> bits = new();
    TimeSpan confettiStart, confettiLast;
    bool confettiRunning;
    record Bit(Shape Shape, RotateTransform Turn) { public double X, Y, Vx, Vy, Spin; }

    // rage click
    bool mouseWasDown;
    readonly List<(DateTime At, int X, int Y)> clicks = new();
    DateTime lastRage = DateTime.MinValue;

    // shy mode
    bool shyAway;
    double shyDx, shyDy;
    DateTime shyFarSince = DateTime.MaxValue, shyHopAt;

    // touch grass / night owl
    DateTime activeSince = DateTime.Now;
    bool grassGrown, nightYawned, petAsleep;
    DateTime grassUntil, waterSince = DateTime.Now;
    bool? nightForced;

    /// <summary>Something fun is showing on the resting island, so it grows a little.</summary>
    bool FunBig => Settings.Current.IslandStyle is "Capsule" or "Outline" &&
        (Settings.Current.Pet || (Settings.Current.TouchGrass && grassGrown) || IsNight || fire > 0.08);
    bool funBigShown;

    /// <summary>Resizes the resting island when the fun things on it come or go.</summary>
    void CheckFunSize()
    {
        if (FunBig == funBigShown) return;
        funBigShown = FunBig;
        UpdateClock();
        if (state == Island.Idle) Morph(Island.Idle);
    }

    bool ShyBlocks => Settings.Current.ShyMode && !FunNative.CtrlDown;
    bool IsNight => Settings.Current.NightOwl && (nightForced ?? DateTime.Now.Hour < 5);

    void InitFun()
    {
        funTick.Tick += (_, _) => FunTick();
        moodTick.Tick += (_, _) => MoodTick();
        ApplyFun();
    }

    /// <summary>Starts or stops each fun feature to match its switch.</summary>
    void ApplyFun()
    {
        var st = Settings.Current;
        if (st.Pet || st.TouchGrass || st.NightOwl || st.WaterReminder) funTick.Start(); else funTick.Stop();
        if (!st.TouchGrass) { grassGrown = false; activeSince = DateTime.Now; }
        if (!st.WaterReminder) waterSince = DateTime.Now;
        if (!st.NightOwl) { nightYawned = false; nightForced = null; }
        if (!st.TypingFire) fire = 0;
        if (!st.ShyMode && shyAway) ShyBack();

        if (st.MoodRing && load == null) load = new SystemLoad();
        else if (!st.MoodRing && load != null) { load.Dispose(); load = null; mood = -1; cpuUse = -1; gpuUse = null; }
        if (st.Temps && temps == null) temps = new Temperatures();
        else if (!st.Temps && temps != null) { temps.Dispose(); temps = null; cpuTemp = gpuTemp = null; }
        if (st.MoodRing || st.Temps) moodTick.Start(); else moodTick.Stop();
        ShowStats();

        if (st.Confetti && bin == null && !Testing) { bin = new RecycleBinWatch(); bin.Emptied += () => Dispatcher.BeginInvoke(BinEmptied); }
        else if (!st.Confetti && bin != null) { bin.Dispose(); bin = null; }

        UpdateNight();
        CheckFunSize();
        UpdateFunLook();
        RimColour();
        if (!glowTick.IsEnabled) Rim.Opacity = RestingRim;
    }

    /// <summary>Called after every morph: the pet and grass only show on the resting island.</summary>
    void UpdateFunLook()
    {
        bool resting = state == Island.Idle && !fullscreenHidden && Settings.Current.IslandStyle is "Capsule" or "Outline";
        bool pet = Settings.Current.Pet && resting && !(pinnedNote != null && !AtMiddle);
        if (pet)
        {
            Pet.HorizontalAlignment = AtMiddle ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            Pet.VerticalAlignment = AtMiddle ? VerticalAlignment.Top : VerticalAlignment.Center;
            Pet.Margin = AtMiddle ? new Thickness(0, 7, 0, 0) : new Thickness(10, 0, 0, 0);
            Pet.LayoutTransform = new ScaleTransform(1.4, 1.4);
        }
        Fade(Pet, pet);
        PetBreathe(pet);
        if (state == Island.Open && (temps != null || load != null)) MoodTick(); // fresh numbers and colour as it opens
        if (!glowTick.IsEnabled) Rim.Opacity = RestingRim;
        Fade(Grass, Settings.Current.TouchGrass && grassGrown && resting && !AtMiddle);
        Grass.Height = 10;
        Grass.Margin = new Thickness(15, 0, 15, 0);
    }

    static void Fade(UIElement el, bool show)
    {
        bool shown = el.Visibility == Visibility.Visible && el.Opacity > 0.01;
        if (show == shown) return;
        if (show)
        {
            el.Visibility = Visibility.Visible;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = TimeSpan.FromMilliseconds(200) });
        }
        else
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 0;
            el.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- the 25 ms check: typing, rage clicks, shy mode ----------

    void FunEdgeTick()
    {
        var st = Settings.Current;
        if (!(st.Pet || st.TypingFire || st.RageClick || st.ShyMode)) return;
        Native.GetCursorPos(out var p);

        // Typing: Windows says *when* there was input, never what. Input while the mouse stayed put is a key.
        if (st.Pet || st.TypingFire)
        {
            uint last = FunNative.LastInput();
            if (last != lastInputTick)
            {
                bool mouseStill = p.X == lastCursor.X && p.Y == lastCursor.Y && !FunNative.LeftButtonDown;
                if (lastInputTick != 0 && mouseStill) Typed();
                lastInputTick = last;
            }
        }

        if (st.RageClick)
        {
            bool down = FunNative.LeftButtonDown;
            if (down && !mouseWasDown) Clicked(p);
            mouseWasDown = down;
        }

        if (st.ShyMode) Shy(p);
        if (st.Pet) PetWatch(p);
        lastCursor = p;
    }

    void Typed()
    {
        if (Settings.Current.Pet && (DateTime.Now - lastTypedAt).TotalMilliseconds > 180) PetSquishOnce();
        lastTypedAt = DateTime.Now;
        if (!Settings.Current.TypingFire) return;
        fire = Math.Min(1, fire + 0.07);
        glowTick.Start();
        CheckFunSize();
    }

    // ---------- typing fire and mood ring: both colour the rim ----------

    /// <summary>The rim's light while nothing else is lighting it: the mood ring's steady glow.</summary>
    double RestingRim => mood >= 0 && IsOpen ? 0.75 : 0; // only around the open panel: the resting island keeps your theme colour

    string rimMode = "";
    readonly SolidColorBrush rimBrush = new();

    void RimColour()
    {
        string mode = state is Island.Alarm or Island.Agent or Island.AgentDot ? "Orange"
            : fire > 0.08 ? "fire" : mood >= 0 && IsOpen ? "mood" : "Green";
        if (mode is "fire" or "mood")
        {
            rimBrush.Color = mode == "fire"
                ? Mix(Color.FromRgb(0xFF, 0xB0, 0x20), Color.FromRgb(0xFF, 0x6B, 0x5E), fire)
                : MoodColour(mood);
            if (rimMode != "custom") { Rim.BorderBrush = rimBrush; rimMode = "custom"; Rim.BorderThickness = new Thickness(2.5); }
            return;
        }
        if (rimMode == mode) return;
        Rim.BorderThickness = new Thickness(1.5);
        Rim.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, mode);
        rimMode = mode;
    }

    /// <summary>Calm sky blue, then lime, amber, and coral when it's flat out.</summary>
    static Color MoodColour(double v)
    {
        Color sky = Color.FromRgb(0x4C, 0xC9, 0xF0), lime = Color.FromRgb(0xB8, 0xF2, 0x3A),
              amber = Color.FromRgb(0xFF, 0xB0, 0x20), coral = Color.FromRgb(0xFF, 0x6B, 0x5E);
        return v < 0.4 ? Mix(sky, lime, v / 0.4) : v < 0.7 ? Mix(lime, amber, (v - 0.4) / 0.3) : Mix(amber, coral, (v - 0.7) / 0.3);
    }

    static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    void MoodTick()
    {
        if (fullscreenHidden) return; // nobody can see it: don't measure
        if (temps != null && IsOpen) { cpuTemp = temps.Cpu(); gpuTemp = temps.Gpu(); ShowStats(); } // temperatures only show on the open panel
        // The GPU counters are the costly part: read them every 2 s while open, every 12 s while closed.
        if (load != null && IsOpen) SetMood(load.Cpu(), load.Gpu()); // closed: nothing to show, nothing measured
    }


    /// <summary>The numbers in the open panel's top bar, like "CPU 34% 52° · GPU 71% 61°".</summary>
    void ShowStats()
    {
        static string Part(string name, double use, double? temp) =>
            name + (use >= 0 ? $" {use:0}%" : "") + (temp is double t ? $" {t:0}°" : "");
        var parts = new List<string>();
        if (cpuUse >= 0 || cpuTemp != null) parts.Add(Part("CPU", cpuUse, cpuTemp));
        if (gpuUse != null || gpuTemp != null) parts.Add(Part("GPU", gpuUse ?? -1, gpuTemp));
        MoodStats.Text = string.Join(" · ", parts);
        MoodStats.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetMood(double cpu, double? gpu)
    {
        mood = Math.Max(cpu, gpu ?? 0) / 100;
        cpuUse = cpu; gpuUse = gpu;
        ShowStats();
        RimColour();
        CheckFunSize();
        if (!glowTick.IsEnabled) Rim.Opacity = RestingRim;
    }

    // ---------- confetti ----------

    static readonly Color[] ConfettiColours =
    {
        Color.FromRgb(0xB8, 0xF2, 0x3A), Color.FromRgb(0xFF, 0xB0, 0x20), Color.FromRgb(0x4C, 0xC9, 0xF0), Color.FromRgb(0xFF, 0x6B, 0x5E), Colors.White,
    };

    /// <summary>A short burst of confetti inside the island (about 1.6 seconds, then nothing runs).</summary>
    void Confetti()
    {
        if (!Settings.Current.Confetti || IsOpen || fullscreenHidden) return;
        ConfettiLayer.Children.Clear();
        bits.Clear();
        double w = width.Target, h = height.Target;
        double scale = Math.Clamp(h / 220, 0.35, 1);
        double x0 = Side switch { HorizontalAlignment.Left => w / 2, HorizontalAlignment.Right => Shell.ActualWidth - w / 2, _ => w / 2 };
        for (int i = 0; i < 34; i++)
        {
            var c = ConfettiColours[rng.Next(ConfettiColours.Length)];
            Shape s = rng.Next(3) == 0 ? new Ellipse { Width = 4, Height = 4 } : new Rectangle { Width = 3, Height = 6, RadiusX = 1, RadiusY = 1 };
            s.Fill = new SolidColorBrush(c);
            var turn = new RotateTransform(rng.Next(360));
            s.RenderTransform = turn;
            s.RenderTransformOrigin = new Point(0.5, 0.5);
            ConfettiLayer.Children.Add(s);
            bits.Add(new Bit(s, turn)
            {
                X = x0 + (rng.NextDouble() - 0.5) * 40,
                Y = h * 0.55,
                Vx = (rng.NextDouble() - 0.5) * 520 * Math.Max(scale, 0.6),
                Vy = -(160 + rng.NextDouble() * 260) * scale,
                Spin = (rng.NextDouble() - 0.5) * 900,
            });
        }
        confettiStart = TimeSpan.Zero;
        if (!confettiRunning) { confettiRunning = true; CompositionTarget.Rendering += ConfettiFrame; }
    }

    void ConfettiFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (confettiStart == TimeSpan.Zero) { confettiStart = confettiLast = now; return; }
        double dt = Math.Min((now - confettiLast).TotalSeconds, 1 / 30.0);
        if (dt <= 0) return;
        confettiLast = now;
        double t = (now - confettiStart).TotalSeconds;
        double g = 620 * Math.Clamp(height.Value / 220, 0.35, 1);
        foreach (var b in bits)
        {
            b.Vy += g * dt;
            b.Vx *= 0.985;
            b.X += b.Vx * dt;
            b.Y += b.Vy * dt;
            b.Turn.Angle += b.Spin * dt;
            Canvas.SetLeft(b.Shape, b.X);
            Canvas.SetTop(b.Shape, b.Y);
            b.Shape.Opacity = t < 1.1 ? 1 : Math.Max(0, 1 - (t - 1.1) / 0.5);
        }
        if (t > 1.6 || IsOpen)
        {
            CompositionTarget.Rendering -= ConfettiFrame;
            confettiRunning = false;
            ConfettiLayer.Children.Clear();
            bits.Clear();
        }
    }

    void BinEmptied()
    {
        if (!Settings.Current.Confetti) return;
        ShowInfo("", "Green", "Recycle Bin emptied", 2400);
        Confetti();
    }

    // ---------- rage click ----------

    void Clicked(Native.POINT p)
    {
        var now = DateTime.Now;
        clicks.Add((now, p.X, p.Y));
        clicks.RemoveAll(c => (now - c.At).TotalSeconds > 2.5 || Math.Abs(c.X - p.X) > 30 * dpi || Math.Abs(c.Y - p.Y) > 30 * dpi);
        if (clicks.Count < 5 || (now - lastRage).TotalSeconds < 20) return;
        clicks.Clear();
        lastRage = now;
        RageMoment();
    }

    void RageMoment() => ShowInfo("", "Coral", "calm down…", 2400);

    // ---------- shy mode ----------

    void Shy(Native.POINT p)
    {
        bool canHide = !IsOpen && !fullscreenHidden && !dragging && state is Island.Idle or Island.Music or Island.Timer;
        if (FunNative.CtrlDown || !canHide) { if (shyAway && (FunNative.CtrlDown || IsOpen)) ShyBack(); return; }

        // where the island is now, in screen pixels
        var r = islandRect;
        r.Offset(Left, Top);
        double cx = (r.Left + r.Width / 2) * dpi, cy = (r.Top + r.Height / 2) * dpi;
        double dx = Math.Max(0, Math.Abs(p.X - cx) - r.Width / 2 * dpi), dy = Math.Max(0, Math.Abs(p.Y - cy) - r.Height / 2 * dpi);
        double near = Math.Sqrt(dx * dx + dy * dy) / dpi;

        if ((DateTime.Now - shyHopAt).TotalMilliseconds < 450) return; // still gliding from the last hop
        if (near < 46)
        {
            shyHopAt = DateTime.Now;
            // run away from the cursor, to the other side of where it normally lives
            double hop = 170;
            if (AtMiddle)
            {
                double dir = p.Y / dpi < r.Top + r.Height / 2 ? 1 : -1;
                shyDy = shyAway && Math.Sign(shyDy) == dir ? -shyDy : dir * hop;
                shyDx = 0;
            }
            else
            {
                double dir = p.X / dpi < r.Left + r.Width / 2 ? 1 : -1;
                if (Side == HorizontalAlignment.Left) dir = 1;
                if (Side == HorizontalAlignment.Right) dir = -1;
                shyDx = shyAway && Side == HorizontalAlignment.Center ? (shyDx > 0 ? -hop : hop) : dir * hop * (shyAway && Side != HorizontalAlignment.Center ? 2 : 1);
                shyDy = 0;
            }
            shyAway = true;
            shyFarSince = DateTime.MaxValue;
            Place(animate: true, quick: true);
            return;
        }
        if (!shyAway) return;
        // gone for good once the cursor has stayed away for a moment
        if (near > 160)
        {
            if (shyFarSince == DateTime.MaxValue) shyFarSince = DateTime.Now;
            else if ((DateTime.Now - shyFarSince).TotalSeconds > 1.5) ShyBack();
        }
        else shyFarSince = DateTime.MaxValue;
    }

    // A WPF animation of Left/Top re-lays out the window on every step, which looked sluggish. This moves the
    // window straight through Windows once per screen refresh (60, 120, 144 Hz... whatever the monitor does).
    double moveFromX, moveFromY, moveToX, moveToY;
    TimeSpan moveStart;
    bool moving;

    void FastMove(double left, double top)
    {
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        moveFromX = moving ? curX : Left; moveFromY = moving ? curY : Top;
        moveToX = left; moveToY = top;
        moveStart = TimeSpan.Zero;
        if (!moving) { moving = true; CompositionTarget.Rendering += MoveFrame; }
    }

    double curX, curY;

    void MoveFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (moveStart == TimeSpan.Zero) moveStart = now;
        double t = Math.Min(1, (now - moveStart).TotalMilliseconds / 210);
        double k = 1 - Math.Pow(1 - t, 4); // fast start, soft landing
        curX = moveFromX + (moveToX - moveFromX) * k;
        curY = moveFromY + (moveToY - moveFromY) * k;
        FunNative.MoveWindow(hwnd, (int)Math.Round(curX * dpi), (int)Math.Round(curY * dpi));
        if (t < 1) return;
        CompositionTarget.Rendering -= MoveFrame;
        moving = false;
        Left = moveToX; Top = moveToY; // let WPF know where it ended up
    }

    void ShyBack()
    {
        shyAway = false;
        shyDx = shyDy = 0;
        shyFarSince = DateTime.MaxValue;
        Place(animate: true, quick: true);
    }

    // ---------- pet, touch grass, night owl (one slow tick) ----------

    void FunTick()
    {
        var st = Settings.Current;
        double idle = FunNative.IdleSeconds();

        if (st.TouchGrass)
        {
            if (idle > 300) { activeSince = DateTime.Now; if (grassGrown) { grassGrown = false; UpdateFunLook(); CheckFunSize(); } } // you took a break
            else if (!grassGrown && DateTime.Now - activeSince >= TimeSpan.FromMinutes(60)) GrowGrass();
            else if (grassGrown && DateTime.Now > grassUntil)
            {
                // the grass only stays a few seconds; the next reminder comes after another hour
                grassGrown = false; activeSince = DateTime.Now;
                UpdateFunLook(); CheckFunSize();
            }
        }

        if (st.WaterReminder)
        {
            if (idle > 300) waterSince = DateTime.Now; // you were away: maybe you had a drink
            else if (DateTime.Now - waterSince >= TimeSpan.FromMinutes(45)) { waterSince = DateTime.Now; Water(); }
        }

        if (st.NightOwl)
        {
            bool night = IsNight;
            if (night != nightLook) UpdateNight();
            if (night && !nightYawned && state == Island.Idle && !fullscreenHidden)
            {
                nightYawned = true;
                ShowInfo("", "Blue", "*yawn* it's past midnight", 2600);
            }
            if (!night) nightYawned = false;
        }

        if (st.Pet && Pet.Visibility == Visibility.Visible)
        {
            bool sleep = idle > 60 || IsNight;
            if (sleep != petAsleep) SetPetAsleep(sleep);
            if (!sleep)
            {
                bool typing = (DateTime.Now - lastTypedAt).TotalSeconds < 2.5;
                if (!typing && rng.Next(3) == 0) Blink();
            }
        }
    }

    /// <summary>The pet's eyes follow your cursor, and look down at the keyboard while you type.</summary>
    void PetWatch(Native.POINT p)
    {
        if (Pet.Visibility != Visibility.Visible || petAsleep || hwnd == IntPtr.Zero) return;
        double x, y;
        if ((DateTime.Now - lastTypedAt).TotalSeconds < 2.5) { x = 0; y = 1.6; }
        else
        {
            var c = Pet.PointToScreen(new Point(Pet.ActualWidth / 2, Pet.ActualHeight / 2));
            double dx = p.X - c.X, dy = p.Y - c.Y, len = Math.Sqrt(dx * dx + dy * dy);
            double reach = Math.Min(1, len / (60 * dpi)); // close to the pet: small moves; far away: eyes at the edge
            x = len < 1 ? 0 : dx / len * 2.4 * reach;
            y = len < 1 ? 0 : dy / len * 1.6 * reach;
        }
        if (Math.Abs(PetLook.X - x) > 0.08 || Math.Abs(PetLook.Y - y) > 0.08) { PetLook.X = x; PetLook.Y = y; }
    }

    // ---------- the pet's body: breathing, a squish when you type, a hop when it wakes ----------

    bool? petBreathing;

    /// <summary>A soft squash-and-stretch, forever while the pet is on screen (24 fps), slower while it sleeps.</summary>
    void PetBreathe(bool on)
    {
        if (petBreathing == on) return;
        petBreathing = on;
        if (!on)
        {
            PetSquish.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PetSquish.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PetSquish.ScaleX = PetSquish.ScaleY = 1;
            return;
        }
        var period = TimeSpan.FromSeconds(petAsleep ? 2.6 : 1.3);
        DoubleAnimation Breath(double to)
        {
            var a = new DoubleAnimation(1, to, period)
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(a, 24);
            return a;
        }
        PetSquish.BeginAnimation(ScaleTransform.ScaleYProperty, Breath(petAsleep ? 0.9 : 0.93));
        PetSquish.BeginAnimation(ScaleTransform.ScaleXProperty, Breath(petAsleep ? 1.06 : 1.04));
    }

    /// <summary>A quick squish, like it's bouncing along to your typing.</summary>
    void PetSquishOnce()
    {
        if (Pet.Visibility != Visibility.Visible || petAsleep) return;
        var down = new DoubleAnimation(0, 1.4, TimeSpan.FromMilliseconds(70)) { AutoReverse = true };
        PetHop.BeginAnimation(TranslateTransform.YProperty, down);
    }

    /// <summary>A happy little hop when you come back.</summary>
    void PetJump()
    {
        if (Pet.Visibility != Visibility.Visible) return;
        var hop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-4.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-1.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(430))));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520))));
        PetHop.BeginAnimation(TranslateTransform.YProperty, hop);
    }

    void Blink()
    {
        var a = new DoubleAnimation(1, 0.1, TimeSpan.FromMilliseconds(80)) { AutoReverse = true };
        PetLidL.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        PetLidR.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void SetPetAsleep(bool sleep)
    {
        bool woke = petAsleep && !sleep;
        petAsleep = sleep;
        petBreathing = null; PetBreathe(Pet.Visibility == Visibility.Visible); // slower breaths while asleep
        if (woke) PetJump();
        PetLidL.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PetLidR.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PetLidL.ScaleY = PetLidR.ScaleY = sleep ? 0.18 : 1;
        PetLook.X = PetLook.Y = 0;
        if (sleep) PetZ.BeginAnimation(OpacityProperty, Breathing(0, 0.9));
        else { PetZ.BeginAnimation(OpacityProperty, null); PetZ.Opacity = 0; }
    }

    void GrowGrass()
    {
        grassGrown = true;
        grassUntil = DateTime.Now.AddSeconds(8);
        CheckFunSize();
        UpdateFunLook();
        ShowInfo("\uE706", "Green", "touch grass · 1 hour on", 3000);
    }

    /// <summary>Every 45 minutes at the PC: a reminder to drink some water.</summary>
    void Water()
    {
        if (fullscreenHidden) { waterSince = DateTime.Now.AddMinutes(-40); return; } // try again in 5 minutes
        ShowInfo("", "Blue", "time for a sip of water", 5000);
    }

    bool nightLook;

    /// <summary>After midnight the resting clock gets sleepy: dimmer, lighter and slanted.</summary>
    void UpdateNight()
    {
        nightLook = IsNight;
        CheckFunSize();
        IdleClock.FontStyle = nightLook ? FontStyles.Italic : FontStyles.Normal;
        IdleClock.FontWeight = nightLook ? FontWeights.Normal : FontWeights.SemiBold;
        IdleClock.Foreground = new SolidColorBrush(nightLook ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));
        UpdateClock();
    }

    // ---------- test commands (only with LIGHT_TEST) ----------

    internal string? FunTest(string cmd, string arg)
    {
        switch (cmd)
        {
            case "type": for (int i = 0; i < int.Parse(arg); i++) Typed(); return $"fire={fire:0.00}";
            case "mood": var m = arg.Split(' '); SetMood(double.Parse(m[0]), m.Length > 1 ? double.Parse(m[1]) : null); return $"mood={mood:0.00}";
            case "confetti": Confetti(); return "burst";
            case "temps": var tc = temps?.Cpu(); var tg = temps?.Gpu(); return $"cpu={tc?.ToString("0.0") ?? "none"} gpu={tg?.ToString("0.0") ?? "none"}";
            case "scroll": Look.Scroller.ScrollToVerticalOffset(double.Parse(arg)); return arg;
            case "rage": RageMoment(); return state.ToString();
            case "water": Water(); return state.ToString();
            case "grassgone": grassUntil = DateTime.Now.AddSeconds(-1); FunTick(); return "grass=" + grassGrown;
            case "grass": activeSince = DateTime.Now.AddHours(-2); GrowGrass(); return "grown";
            case "night": nightForced = arg == "on"; nightYawned = false; UpdateNight(); FunTick(); return "night=" + IsNight;
            case "pethop": PetJump(); return "hop";
            case "petsleep": SetPetAsleep(arg == "on"); return "asleep=" + petAsleep;
            case "bin": BinEmptied(); return state.ToString();
            case "shy": shyDx = arg == "off" ? 0 : 170; shyAway = arg != "off"; Place(animate: false); return "shy=" + shyAway;
            default: return null;
        }
    }
}
