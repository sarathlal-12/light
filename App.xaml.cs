using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WF = System.Windows.Forms;

namespace DotLight;

public partial class App : Application
{
    /// <summary>True while a file/folder dialog is open, so the panel doesn't collapse under it.</summary>
    public static bool DialogOpen;

    Mutex? mutex;
    WF.NotifyIcon? tray;
    NotchWindow? notch;

    protected override void OnStartup(StartupEventArgs e)
    {
        mutex = new Mutex(true, "DotLight.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        // WPF animates at 60 fps by default; let it follow faster screens for smoother motion.
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata { DefaultValue = 120 });

        base.OnStartup(e);
        DispatcherUnhandledException += OnCrash;

        Theme.Init(this); // live accent colours, before any window uses them
        notch = new NotchWindow();
        notch.Show();
        TestDriver.Start(notch);

        Motion.EnableButtonSquash();

        var menu = new WF.ContextMenuStrip();
        var openItem = menu.Items.Add("Open .light", null, (_, _) => notch.Toggle(byKey: true));
        var grabItem = menu.Items.Add("Grab text from screen", null, (_, _) => notch.GrabFromTray());
        var grabImageItem = menu.Items.Add("Grab image from screen", null, (_, _) => notch.GrabImageFromTray());
        var autostart = new WF.ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsOn, CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => Autostart.Set(autostart.Checked);
        menu.Items.Add(autostart);

        var position = new WF.ToolStripMenuItem("Position");
        foreach (var (key, label) in NotchWindow.Positions)
        {
            var item = new WF.ToolStripMenuItem(label) { Tag = key };
            item.Click += (_, _) => notch.SetPosition(key, animate: true);
            position.DropDownItems.Add(item);
        }
        menu.Items.Insert(3, position);
        menu.Opening += (_, _) =>
        {
            openItem.Text = $"Open .light   ({Settings.Current.HotkeyOpen})";
            grabItem.Text = $"Grab text from screen   ({Settings.Current.HotkeyGrab})";
            grabImageItem.Text = $"Grab image from screen   ({Settings.Current.HotkeyGrabImage})";
            grabItem.Visible = Settings.Current.GrabTextOn;
            grabImageItem.Visible = Settings.Current.GrabImageOn;
            autostart.Checked = Autostart.IsOn;
            foreach (WF.ToolStripMenuItem item in position.DropDownItems)
                item.Checked = (string)item.Tag! == Settings.Current.Position;
        };
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Quit .light", null, (_, _) => Shutdown());

        tray = new WF.NotifyIcon { Icon = MakeTrayIcon(), Text = ".light", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, a) => { if (a.Button == WF.MouseButtons.Left) notch.Toggle(byKey: true); };

        if (!notch.HotkeyOk)
            tray.ShowBalloonTip(5000, ".light", $"{Settings.Current.HotkeyOpen} is taken by another app. Hover the island to open .light, then pick a new shortcut in Look (the gear).", WF.ToolTipIcon.Info);
        else if (!Settings.Current.Welcomed)
            tray.ShowBalloonTip(5000, ".light is running", $"Move your cursor to the top centre of the screen, or press {Settings.Current.HotkeyOpen}.", WF.ToolTipIcon.None);

        Settings.Current.Welcomed = true;
        Settings.Current.Save();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Settings.Current.Save();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        mutex?.Dispose();
        base.OnExit(e);
    }

    void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(Path.Combine(Settings.Dir, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch { }
        e.Handled = true; // keep the notch alive
        if (notch == null) Shutdown(); // failed while starting: don't linger invisibly with no island
    }

    /// <summary>The app icon (Assets\light.ico), at the tray's size for this screen's scaling.</summary>
    static Drawing.Icon MakeTrayIcon()
    {
        using var s = typeof(App).Assembly.GetManifestResourceStream("light.ico")!;
        return new Drawing.Icon(s, WF.SystemInformation.SmallIconSize);
    }
}

static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = ".light";

    public static bool IsOn
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) != null;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, false);
    }
}
