using System.IO;
using System.Text.Json;

namespace DotLight;

/// <summary>Small settings file at %APPDATA%\.light\settings.json.</summary>
class Settings
{
    // LIGHT_DATA (testing only) points .light at another folder, so test runs never touch your own data
    public static string Dir { get; } =
        Environment.GetEnvironmentVariable("LIGHT_DATA") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".light");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public bool Welcomed { get; set; }

    /// <summary>Where the island sits: TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter or BottomRight.</summary>
    public string Position { get; set; } = "TopCenter";

    /// <summary>Colour of the light: iOS, a preset name, Album or Windows.</summary>
    public string Light { get; set; } = "iOS";

    /// <summary>How the resting island looks: Capsule, Line, Outline or Dot.</summary>
    public string IslandStyle { get; set; } = "Capsule";

    /// <summary>Show the time in the resting island.</summary>
    public bool IdleClock { get; set; } = true;

    /// <summary>The rim light slowly breathes while nothing is happening.</summary>
    public bool Breathing { get; set; } = true;

    /// <summary>Shortcut that opens/closes the panel.</summary>
    public string HotkeyOpen { get; set; } = "Alt+Space";

    /// <summary>Shortcut that starts "grab text from the screen".</summary>
    public string HotkeyGrab { get; set; } = "Ctrl+Alt+T";

    /// <summary>Shortcut that starts "grab image from the screen".</summary>
    public string HotkeyGrabImage { get; set; } = "Ctrl+Alt+S";

    // Features that can be switched off in Look > Features.
    public bool ShowTimer { get; set; } = true;
    public bool ShowNotes { get; set; } = true;
    public bool ShowFileLab { get; set; } = true;
    public bool ShowSnippets { get; set; } = true;
    public bool ShowShelf { get; set; } = true;
    public bool GrabTextOn { get; set; } = true;
    public bool GrabImageOn { get; set; } = true;

    /// <summary>Claude Code tells .light when it needs your OK or has finished (adds a hook to Claude Code's settings).</summary>
    public bool AgentStatus { get; set; }

    // Moments that can be switched off in Look > Features (all on by default).
    public bool ShotMoment { get; set; } = true;
    public bool PrivacyDots { get; set; } = true;
    public bool PowerMoment { get; set; } = true;
    public bool DownloadMoment { get; set; } = true;
    public bool UpdateNotice { get; set; } = true;

    /// <summary>Clipboard history is paused: new copies aren't saved.</summary>
    public bool ClipsPaused { get; set; }

    /// <summary>The newest version the update notice has already shown, so it shows only once.</summary>
    public string? UpdateShown { get; set; }

    /// <summary>Last minutes picked on the timer dial.</summary>
    public int TimerMinutes { get; set; } = 25;

    /// <summary>Converter: remove location and camera details from photos.</summary>
    public bool StripPhotoInfo { get; set; } = true;

    public static Settings Current { get; } = Load();

    static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt file: start fresh */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not worth crashing over */ }
    }
}
