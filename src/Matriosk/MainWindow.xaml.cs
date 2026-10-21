using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Matriosk;

public partial class MainWindow : Window
{
    private readonly Pipeline pipeline;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private FileSystemWatcher? watcher;
    private DevPanel? devPanel;
    private ChangeFlags pendingChanges;
    private bool reloadPending;
    private bool layoutPending;
    private bool configDirty;
    private bool screensaverMode;
    private bool mouseArmed;
    private Point mouseAnchor;
    private DispatcherTimer? armTimer;
    private DateTime suppressReloadUntil = DateTime.MinValue;
    private double lastFpsTime;
    private int fpsFrames;
    private double fps;
    private double lastTick;

    internal Pipeline Pipeline => pipeline;

    /// <summary>Accumulates what the tuning panel changed; applied at the next tick.</summary>
    internal void RequestChange(ChangeFlags flags) => pendingChanges |= flags;

    /// <summary>Ignores the next watcher event, so saving from the panel doesn't reload.</summary>
    internal void SuppressReload() => suppressReloadUntil = DateTime.UtcNow.AddMilliseconds(900);

    /// <summary>
    /// Unsaved panel edits outlive the panel window, so hot reload must not clobber
    /// them after it is closed.
    /// </summary>
    internal void SetConfigDirty(bool dirty) => configDirty = dirty;

    public MainWindow()
    {
        InitializeComponent();

        pipeline = new Pipeline(Config.Load(App.ConfigPath));
        timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(1.0 / Math.Max(10, pipeline.Config.Fps)),
        };
        timer.Tick += OnTick;

        Loaded += OnLoaded;
        SizeChanged += (_, _) => layoutPending = true;
        DpiChanged += (_, _) => layoutPending = true;
        Closed += (_, _) => watcher?.Dispose();
        MouseMove += OnScreensaverMouseMove;
        MouseDown += OnScreensaverMouseDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Relayout();
        ApplyScreensaverMode();

        lastTick = clock.Elapsed.TotalSeconds;
        timer.Start();
        StartWatcher();
        if (App.StartWithPanel) TogglePanel();
    }

    private void Relayout()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, Root.ActualWidth);
        var height = Math.Max(1, Root.ActualHeight);

        pipeline.Layout(width, height, dpi.DpiScaleX);

        Field.Width = width;
        Field.Height = height;
        if (!ReferenceEquals(Field.Source, pipeline.Renderer.Bitmap))
        {
            Field.Source = pipeline.Renderer.Bitmap;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Clamp(now - lastTick, 0, 0.1);
        lastTick = now;

        if (reloadPending)
        {
            reloadPending = false;
            if (!configDirty && DateTime.UtcNow >= suppressReloadUntil)
            {
                Reload();
                devPanel?.Refresh();
            }
        }

        if (pendingChanges != ChangeFlags.None)
        {
            ApplyPendingChanges();
        }

        var desiredInterval = TimeSpan.FromSeconds(1.0 / Math.Max(10, pipeline.Config.Fps));
        if (Math.Abs((timer.Interval - desiredInterval).TotalMilliseconds) > 1)
        {
            timer.Interval = desiredInterval;
        }

        if (layoutPending)
        {
            layoutPending = false;
            Relayout();
        }

        var frame = Stopwatch.StartNew();
        pipeline.Step(dt);
        pipeline.Draw();
        pipeline.LastFrameMs = frame.Elapsed.TotalMilliseconds;

        fpsFrames++;
        if (now - lastFpsTime >= 0.5)
        {
            fps = fpsFrames / (now - lastFpsTime);
            fpsFrames = 0;
            lastFpsTime = now;
        }

        if (Stats.Visibility == Visibility.Visible) UpdateStats();
    }

    private void ApplyPendingChanges()
    {
        var flags = pendingChanges;
        pendingChanges = ChangeFlags.None;

        if ((flags & ChangeFlags.Layout) != 0)
        {
            Relayout();
            return;
        }

        if ((flags & ChangeFlags.Glyphs) != 0)
        {
            pipeline.RebuildMasks();
        }

        if ((flags & ChangeFlags.Mask) != 0)
        {
            pipeline.RebuildMask();
        }

        if ((flags & ChangeFlags.Palettes) != 0)
        {
            pipeline.Renderer.BuildPalettes(pipeline.Config);
        }

        // screensaver is deliberately not live-applied: entering full screen with
        // quit-on-input from the panel would leave no way back. It applies on load.

        // Rolled values may have changed too (ranges are sampled per roll).
        pipeline.ResampleRainRate();
    }

    /// <summary>
    /// Screensaver mode: borderless full screen, hidden cursor, and any input quits.
    /// </summary>
    private void ApplyScreensaverMode()
    {
        var want = pipeline.Config.Screensaver;
        if (want == screensaverMode) return;
        screensaverMode = want;

        if (want)
        {
            devPanel?.Hide();
            Stats.Visibility = Visibility.Collapsed;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Topmost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.None;
            layoutPending = true;

            // Ignore the initial pointer position; arm after a short grace period.
            mouseArmed = false;
            armTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            armTimer.Tick -= OnArmScreensaverInput;
            armTimer.Tick += OnArmScreensaverInput;
            armTimer.Start();
        }
        else
        {
            armTimer?.Stop();
            mouseArmed = false;

            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            Width = 1280;
            Height = 800;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
            Topmost = false;
            ShowInTaskbar = true;
            Cursor = Cursors.Arrow;
            layoutPending = true;
        }
    }

    private void OnArmScreensaverInput(object? sender, EventArgs e)
    {
        armTimer?.Stop();
        mouseAnchor = Mouse.GetPosition(this);
        mouseArmed = true;
    }

    private void OnScreensaverMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (pipeline.Config.Screensaver) Close();
    }

    private void OnScreensaverMouseMove(object sender, MouseEventArgs e)
    {
        if (!pipeline.Config.Screensaver || !mouseArmed) return;

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - mouseAnchor.X) > 4 || Math.Abs(position.Y - mouseAnchor.Y) > 4)
        {
            Close();
        }
    }

    private void TogglePanel()
    {
        if (pipeline.Config.Screensaver) return;

        if (devPanel is { IsVisible: true })
        {
            devPanel.Hide();
            return;
        }

        // A closed window cannot be shown again, so build a fresh one. Values come
        // from the live config, so opening it is always in sync.
        if (devPanel is null || !devPanel.IsLoaded)
        {
            devPanel = new DevPanel(this);
            devPanel.Closed += (_, _) => devPanel = null;
        }

        devPanel.ShowPanel();
    }

    private void UpdateStats()
    {
        var renderer = pipeline.Renderer;
        Stats.Text =
            $"fps      {fps,6:0.0}\n" +
            $"frame    {pipeline.LastFrameMs,6:0.00} ms\n" +
            $"grid     {pipeline.Cols} x {pipeline.Rows} = {pipeline.Cols * pipeline.Rows:N0} cells\n" +
            $"cell     {pipeline.CellW} x {pipeline.CellH} px @ {pipeline.DpiScale:0.##}x\n" +
            $"lit      {renderer.Visible:N0} (hot {renderer.Hot:N0})\n" +
            $"drops    {pipeline.Rain.Count}  ripples {pipeline.Ripples.Count}\n" +
            $"text     \"{pipeline.Config.Text}\"";
    }

    private void Reload()
    {
        var loaded = Config.Load(App.ConfigPath);
        pipeline.Config.CopyFrom(loaded);
        pipeline.Config.Normalize();
        timer.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(10, pipeline.Config.Fps));
        Relayout();
        ApplyScreensaverMode();
    }

    private void StartWatcher()
    {
        try
        {
            var directory = Path.GetDirectoryName(App.ConfigPath);
            if (directory is null) return;

            watcher = new FileSystemWatcher(directory, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Changed += (_, _) => Dispatcher.BeginInvoke(() => reloadPending = true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[matriosk] watcher failed: {ex.Message}");
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)    {
        base.OnKeyDown(e);

        if (pipeline.Config.Screensaver)
        {
            Close();
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.D:
                Stats.Visibility = Stats.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                if (Stats.Visibility == Visibility.Visible) UpdateStats();
                break;
            case Key.F:
                ToggleFullscreen();
                break;
            case Key.R:
                Reload();
                break;
            case Key.OemTilde:
                TogglePanel();
                break;
        }
    }

    private void ToggleFullscreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
    }
}
