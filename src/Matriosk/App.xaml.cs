using System.IO;
using System.Windows;

namespace Matriosk;

public partial class App : Application
{
    internal static bool StartWithPanel { get; private set; }

    private void OnStartupCore(object sender, StartupEventArgs e)
    {
        var args = e.Args;
        StartWithPanel = args.Any(a => a.Equals("--panel", StringComparison.OrdinalIgnoreCase));

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Headless.SelfTest();
            Shutdown(0);
            return;
        }
        var benchIndex = Array.FindIndex(args, a => a.Equals("--bench", StringComparison.OrdinalIgnoreCase));
        if (benchIndex >= 0)
        {
            var frames = args.Length > benchIndex + 1 && int.TryParse(args[benchIndex + 1], out var f) ? f : 300;
            Headless.Bench(frames, ParseSize(args) ?? (1600, 900));
            Shutdown(0);
            return;
        }

        var shotIndex = Array.FindIndex(args, a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
        if (shotIndex >= 0)
        {
            var path = args.Length > shotIndex + 1 ? args[shotIndex + 1] : "shot.png";
            var frames = args.Length > shotIndex + 2 && int.TryParse(args[shotIndex + 2], out var f) ? f : 120;
            Headless.Shot(path, frames, ParseSize(args) ?? (1600, 900));
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static (int Width, int Height)? ParseSize(string[] args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--size=", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = arg["--size=".Length..].Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
            {
                return (w, h);
            }
        }
        return null;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        OnStartupCore(this, e);
    }

    internal static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");
}
