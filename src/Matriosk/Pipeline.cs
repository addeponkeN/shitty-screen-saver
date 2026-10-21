namespace Matriosk;

/// <summary>
/// Owns the simulation and the renderer; shared by the window and the headless
/// bench/shot modes.
/// </summary>
public sealed class Pipeline
{
    private readonly NoiseField noise = new();

    public Config Config { get; }
    public CharGrid Grid { get; } = new();
    public RainField Rain { get; } = new();
    public RippleField Ripples { get; } = new();
    public FieldRenderer Renderer { get; } = new();
    public GlyphMasks Masks { get; private set; } = new();

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CellW { get; private set; } = 7;
    public int CellH { get; private set; } = 12;
    public float DpiScale { get; private set; } = 1;

    /// <summary>0..1 startup fade, applied to the whole scene.</summary>
    public float FadeCurrent { get; private set; } = 1;

    /// <summary>Disabled for benchmark/screenshot runs so they capture the steady state.</summary>
    public bool FadeInEnabled { get; set; } = true;

    private float elapsed;

    public float NoiseTime { get; private set; }
    public float TextFlash { get; private set; }
    public double LastFrameMs { get; set; }

    /// <summary>Rain spawn rate; the only scene-wide value that can be randomised.</summary>
    public float RainPerSecond { get; private set; }

    private double rollClock;
    private double nextRoll;

    public Pipeline(Config config)
    {
        Config = config;
        Config.Normalize();
        RainPerSecond = Config.Rain.PerSecond.Sample();
        FadeCurrent = Config.FadeIn <= 0 ? 1 : 0;
        ResetRollClock();
    }

    /// <summary>Recomputes the grid, glyph masks and canvas for a client size in DIPs.</summary>
    public void Layout(double widthDip, double heightDip, double dpiScale)
    {
        DpiScale = (float)Math.Max(0.5, dpiScale);
        RainPerSecond = Config.Rain.PerSecond.Sample();
        ResetRollClock();

        var pixelsW = Math.Max(1, (int)Math.Round(widthDip * DpiScale));
        var pixelsH = Math.Max(1, (int)Math.Round(heightDip * DpiScale));

        var cellW = Config.Cell.W * DpiScale;
        var cellH = Config.Cell.H * DpiScale;
        var cols = Math.Max(1, (int)Math.Floor(pixelsW / cellW));
        var rows = Math.Max(1, (int)Math.Floor(pixelsH / cellH));

        var overshoot = cols * (double)rows / Math.Max(1, Config.Cell.MaxCells);
        if (overshoot > 1)
        {
            var scale = MathF.Sqrt((float)overshoot);
            cellW *= scale;
            cellH *= scale;
            cols = Math.Max(1, (int)Math.Floor(pixelsW / cellW));
            rows = Math.Max(1, (int)Math.Floor(pixelsH / cellH));
        }

        CellW = Math.Max(1, (int)Math.Round(cellW));
        CellH = Math.Max(1, (int)Math.Round(cellH));
        Cols = cols;
        Rows = rows;

        Grid.Resize(Cols, Rows);
        Grid.ClampChars(Config.ResolveCharset(Config.Charset).Length);
        Masks = GlyphMasks.Build(
            Config.ResolveCharset(Config.Charset),
            Config.Font,
            Config.Cell.FontWeight,
            Config.Cell.FontScale,
            CellW,
            CellH,
            Config.Colors.Glow);
        Renderer.Configure(Cols, Rows, CellW, CellH, Masks, Config);
        RebuildMask();
    }

    public void RebuildMask()
    {
        Grid.SetWeight(TextMaskBuilder.Build(Config, Cols, Rows));
    }

    public void RebuildMasks()
    {
        var charset = Config.ResolveCharset(Config.Charset);
        // Char indices were drawn from the previous charset; keep them valid before
        // the renderer looks them up.
        Grid.ClampChars(charset.Length);
        Masks = GlyphMasks.Build(
            charset,
            Config.Font,
            Config.Cell.FontWeight,
            Config.Cell.FontScale,
            CellW,
            CellH,
            Config.Colors.Glow);
        Renderer.Configure(Cols, Rows, CellW, CellH, Masks, Config);
    }

    public void Step(float dt)
    {
        elapsed += dt;
        FadeCurrent = Config.FadeIn <= 0 || !FadeInEnabled
            ? 1
            : SmoothStep(Math.Min(1, elapsed / Config.FadeIn));

        AdvanceRoll(dt);

        var rain = Rain.Update(dt, Grid, Config.Rain, RainPerSecond);
        if (rain.HasContact) Ripples.Spawn(rain.ContactX, rain.ContactY, Config.Ripple);
        Ripples.Update(dt, Grid, Config);

        var frameFactor = MathF.Pow(Config.TextMask.Decay, dt * 60);
        TextFlash *= frameFactor;
        var flash = Math.Min(1, rain.WordHit * Config.TextMask.Gain);
        if (flash > TextFlash) TextFlash = flash;

        Grid.Churn(Config.ChurnBuckets, Config.ResolveCharset(Config.Charset).Length);
        Grid.Decay(
            MathF.Pow(Config.Energy.Decay, dt * 60),
            frameFactor,
            Config.TextMask.Floor,
            Config.Energy.MinVisible * 0.25f,
            TextFlash);

        NoiseTime += dt;
        noise.Fill(Grid.Base, Cols, Rows, new NoiseSettings(Config.Noise.Amount, Config.Noise.Scale, Config.Noise.Speed, Config.Noise.Drift, Config.Noise.Roughness), NoiseTime);
    }

    /// <summary>Re-samples the rolled rain rate, e.g. after a config edit.</summary>
    public void ResampleRainRate()
    {
        RainPerSecond = Config.Rain.PerSecond.Sample();
        ResetRollClock();
    }

    /// <summary>Re-samples the rolled rain rate every Randomize.Seconds (0 = once per load).</summary>
    private void AdvanceRoll(float dt)
    {
        if (!double.IsFinite(nextRoll)) return;

        rollClock += dt;
        if (rollClock < nextRoll) return;

        RainPerSecond = Config.Rain.PerSecond.Sample();
        nextRoll = rollClock + Math.Max(0.1, Config.Randomize.Seconds);
    }

    private void ResetRollClock()
    {
        rollClock = 0;
        nextRoll = Config.Randomize.Seconds <= 0 ? double.PositiveInfinity : Config.Randomize.Seconds;
    }

    public void Draw() => Renderer.Draw(Grid, Config, FadeInEnabled ? FadeCurrent : 1f);

    private static float SmoothStep(float t) => t * t * (3 - 2 * t);

    public void Warmup(float seconds)
    {
        var steps = Math.Min(60 * 600, Math.Max(0, (int)(seconds * 60)));
        for (var i = 0; i < steps; i++)
        {
            Step(1f / 60);
            if (i % 60 == 0) Draw();
        }
        Draw();
    }
}
