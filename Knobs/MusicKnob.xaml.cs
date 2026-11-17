using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using MediaManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace DotLight.Knobs;

/// <summary>
/// Music knob: shows and controls whatever is playing on the PC (Spotify, a YouTube tab, etc.).
/// It reads the same "now playing" info Windows shows in its own media flyout.
/// </summary>
public partial class MusicKnob : UserControl
{
    MediaManager? manager;
    MediaSession? session;
    readonly DispatcherTimer refreshSoon = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    bool playing, seeking;
    TimeSpan position, duration;
    DateTimeOffset positionUpdated;
    string trackKey = "";

    /// <summary>Raised when playback starts/stops or the cover changes: (isPlaying, cover).</summary>
    public event Action<bool, ImageSource?>? NowPlayingChanged;

    /// <summary>Raised when a new song starts: (title, artist, cover).</summary>
    public event Action<string, string, ImageSource?>? TrackChanged;

    public MusicKnob()
    {
        InitializeComponent();
        refreshSoon.Tick += async (_, _) => { refreshSoon.Stop(); await Refresh(); };
        tick.Tick += (_, _) => ShowProgress();
        Loaded += async (_, _) => await Connect();
    }

    async Task Connect()
    {
        if (manager != null) return;
        try
        {
            manager = await MediaManager.RequestAsync();
        }
        catch
        {
            return; // media sessions unavailable on this Windows build; knob just shows "Nothing playing"
        }
        manager.CurrentSessionChanged += (_, _) => Dispatcher.BeginInvoke(() => { Attach(manager.GetCurrentSession()); RefreshSoon(); });
        Attach(manager.GetCurrentSession());
        await Refresh();
    }

    void Attach(MediaSession? s)
    {
        if (session == s) return;
        if (session != null)
        {
            session.MediaPropertiesChanged -= OnSessionEvent;
            session.PlaybackInfoChanged -= OnSessionEvent;
            session.TimelinePropertiesChanged -= OnSessionEvent;
        }
        session = s;
        if (session != null)
        {
            session.MediaPropertiesChanged += OnSessionEvent;
            session.PlaybackInfoChanged += OnSessionEvent;
            session.TimelinePropertiesChanged += OnSessionEvent;
        }
    }

    // Session events arrive on background threads and often in bursts; collapse them into one refresh.
    void OnSessionEvent(MediaSession sender, object args) => Dispatcher.BeginInvoke(RefreshSoon);
    void RefreshSoon() { refreshSoon.Stop(); refreshSoon.Start(); }

    async Task Refresh()
    {
        var s = session;
        if (s == null)
        {
            ShowNothing();
            return;
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties? props;
        try { props = await s.TryGetMediaPropertiesAsync(); }
        catch { props = null; }
        if (s != session) return; // switched while we were waiting

        if (props == null || string.IsNullOrWhiteSpace(props.Title))
        {
            ShowNothing();
            return;
        }

        var info = s.GetPlaybackInfo();
        var timeline = s.GetTimelineProperties();

        Empty.Visibility = Visibility.Collapsed;
        Player.Visibility = Visibility.Visible;
        TitleText.Text = props.Title;
        ArtistText.Text = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumTitle : props.Artist;
        AppText.Text = FriendlyAppName(s.SourceAppUserModelId).ToUpperInvariant();

        PrevBtn.IsEnabled = info.Controls.IsPreviousEnabled;
        NextBtn.IsEnabled = info.Controls.IsNextEnabled;
        PlayBtn.IsEnabled = info.Controls.IsPlayPauseToggleEnabled || info.Controls.IsPlayEnabled || info.Controls.IsPauseEnabled;

        bool nowPlaying = info.PlaybackStatus == PlaybackStatus.Playing;
        PlayBtn.Content = nowPlaying ? "" : "";

        duration = timeline.EndTime - timeline.StartTime;
        position = timeline.Position - timeline.StartTime;
        positionUpdated = timeline.LastUpdatedTime;
        ProgressRow.Visibility = duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        ShowProgress();

        // Only reload the cover when the song changes.
        var key = s.SourceAppUserModelId + "|" + props.Title + "|" + props.Artist;
        bool coverChanged = key != trackKey;
        if (coverChanged)
        {
            trackKey = key;
            Cover.ImageSource = await LoadCover(props);
        }

        if (nowPlaying != playing || coverChanged)
        {
            playing = nowPlaying;
            if (playing) tick.Start(); else tick.Stop();
            NowPlayingChanged?.Invoke(playing, Cover.ImageSource);
        }

        if (coverChanged && nowPlaying)
            TrackChanged?.Invoke(props.Title, ArtistText.Text ?? "", Cover.ImageSource);
    }

    void ShowNothing()
    {
        Empty.Visibility = Visibility.Visible;
        Player.Visibility = Visibility.Collapsed;
        tick.Stop();
        trackKey = "";
        Cover.ImageSource = null;
        if (playing)
        {
            playing = false;
            NowPlayingChanged?.Invoke(false, null);
        }
    }

    static async Task<ImageSource?> LoadCover(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail == null) return null;
        try
        {
            using var winStream = await props.Thumbnail.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.DecodePixelWidth = 300;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → "Spotify", "chrome.exe" → "Chrome".</summary>
    static string FriendlyAppName(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("msedge")) return "Edge";
        if (lower.Contains("chrome")) return "Chrome";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("brave")) return "Brave";
        if (lower.Contains("opera")) return "Opera";
        if (lower.Contains("zunemusic") || lower.Contains("media player")) return "Media Player";
        if (lower.Contains("vlc")) return "VLC";
        if (lower.Contains("applemusic") || lower.Contains("itunes")) return "Apple Music";

        var name = id.Contains('!') ? id[(id.LastIndexOf('!') + 1)..] : id;
        name = Path.GetFileNameWithoutExtension(name);
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "Now playing";
    }

    /// <summary>Jumps forwards/backwards by some seconds. Returns the new position, or null if this player can't seek.</summary>
    public async Task<TimeSpan?> SeekBy(double seconds)
    {
        if (session == null || duration <= TimeSpan.Zero) return null;
        var target = CurrentPosition() + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > duration) target = duration;
        try
        {
            if (!await session.TryChangePlaybackPositionAsync(target.Ticks)) return null;
        }
        catch { return null; }
        position = target;
        positionUpdated = DateTimeOffset.Now;
        ShowProgress();
        return target;
    }

    // ---------- light ----------

    /// <summary>The cover in the panel (the island's tiny cover flies into it on open).</summary>
    public FrameworkElement CoverElement => CoverBox;

    /// <summary>The bars next to the app name; the island drives them from the live sound level.</summary>
    public Panel WavePanel => Wave;

    public bool IsPlayerShown => Player.Visibility == Visibility.Visible;

    System.Windows.Media.Effects.DropShadowEffect? seekGlow;

    /// <summary>The played part of the progress bar gets a soft glow in the light colour.</summary>
    void Seek_Loaded(object sender, RoutedEventArgs e)
    {
        if (seekGlow != null) return;
        Seek.ApplyTemplate();
        if (Seek.Template.FindName("PART_Track", Seek) is not System.Windows.Controls.Primitives.Track track) return;
        seekGlow = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 9, Opacity = 0.5, Color = Theme.Current("Green") };
        Theme.Changed += () => seekGlow.Color = Theme.Current("Green");
        if (track.DecreaseRepeatButton != null) track.DecreaseRepeatButton.Effect = seekGlow;
    }

    /// <summary>Called with the live sound level (0–1) so the progress glow breathes with the music.</summary>
    public void SetLevel(double level)
    {
        if (seekGlow != null) seekGlow.Opacity = 0.35 + 0.65 * level;
    }

    // ---------- progress ----------

    TimeSpan CurrentPosition()
    {
        // Apps only report position now and then; estimate it in between while playing.
        var p = playing ? position + (DateTimeOffset.Now - positionUpdated) : position;
        if (p < TimeSpan.Zero) p = TimeSpan.Zero;
        return p > duration ? duration : p;
    }

    void ShowProgress()
    {
        if (duration <= TimeSpan.Zero || seeking) return;
        var p = CurrentPosition();
        Seek.Maximum = duration.TotalSeconds;
        Seek.Value = p.TotalSeconds;
        PosText.Text = Format(p);
        DurText.Text = Format(duration);
    }

    static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    void Seek_Down(object sender, MouseButtonEventArgs e) => seeking = true;

    async void Seek_Up(object sender, MouseButtonEventArgs e)
    {
        // Let the slider move to where you clicked first.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
        seeking = false;
        if (session == null) return;

        var target = TimeSpan.FromSeconds(Seek.Value);
        try
        {
            if (await session.TryChangePlaybackPositionAsync(target.Ticks))
            {
                position = target;
                positionUpdated = DateTimeOffset.Now;
            }
        }
        catch { }
        ShowProgress();
    }

    // ---------- buttons ----------

    async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (session != null) try { await session.TryTogglePlayPauseAsync(); } catch { }
    }

    async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (session != null) try { await session.TrySkipNextAsync(); } catch { }
    }

    async void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (session != null) try { await session.TrySkipPreviousAsync(); } catch { }
    }
}
