using Velopack;

namespace DotLight;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Claude Code hook: hand the event to the running .light and exit straight away.
        if (args.Length > 0 && args[0] == "--agent") return Agent.ForwardFromHook();

        // Must run first: lets the installer/uninstaller run their hooks, then exits if it was one of them.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => { try { Autostart.Set(false); Agent.RemoveHook(); } catch { } })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
        return 0;
    }
}
