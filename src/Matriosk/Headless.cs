using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Matriosk;

/// <summary>Bench and screenshot modes, so the renderer can be measured without a window.</summary>
internal static class Headless
{
    public static void Bench(int frames, (int Width, int Height) size)
    {
        var config = Config.Load(App.ConfigPath);
        var pipeline = new Pipeline(config) { FadeInEnabled = false };
        pipeline.Layout(size.Width, size.Height, 1);
        pipeline.Warmup(2);

        var stages = StageBreakdown(pipeline, Math.Max(50, frames / 2));

        var times = new double[frames];
        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var dt = 1f / 30;
        var budget = TimeSpan.FromSeconds(1.0 / Math.Max(10, config.Fps));
        var simTotal = 0.0;
        var drawTotal = 0.0;

        for (var i = 0; i < frames; i++)
        {
            var frame = Stopwatch.StartNew();

            var sim = Stopwatch.StartNew();
            pipeline.Step(dt);
            simTotal += sim.Elapsed.TotalMilliseconds;

            var draw = Stopwatch.StartNew();
            pipeline.Draw();
            drawTotal += draw.Elapsed.TotalMilliseconds;

            times[i] = frame.Elapsed.TotalMilliseconds;

            // Idle out the rest of the frame budget, like the window timer does.
            var remaining = budget - frame.Elapsed;
            while (remaining > TimeSpan.FromMilliseconds(1))
            {
                Thread.Sleep(1);
                remaining = budget - frame.Elapsed;
            }
        }

        wall.Stop();
        var cpu = process.TotalProcessorTime - cpuStart;

        Array.Sort(times);
        var average = times.Average();
        var p95 = times[(int)(times.Length * 0.95)];
        var cpuPercent = cpu.TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100;

        var maskCells = 0;
        foreach (var weight in pipeline.Grid.Weight)
        {
            if (weight > 0.15f) maskCells++;
        }

        var report = new
        {
            frames,
            size = $"{size.Width}x{size.Height}",
            grid = $"{pipeline.Cols}x{pipeline.Rows}",
            cells = pipeline.Cols * pipeline.Rows,
            lit = pipeline.Renderer.Visible,
            hot = pipeline.Renderer.Hot,
            maskCells,
            frameAvgMs = Math.Round(average, 2),
            frameP95Ms = Math.Round(p95, 2),
            frameMaxMs = Math.Round(times[^1], 2),
            simAvgMs = Math.Round(simTotal / frames, 2),
            drawAvgMs = Math.Round(drawTotal / frames, 2),
            fps = config.Fps,
            drawClearMs = Math.Round(pipeline.Renderer.ClearMs, 2),
            drawBlendMs = Math.Round(pipeline.Renderer.BlendMs, 2),
            drawUploadMs = Math.Round(pipeline.Renderer.UploadMs, 2),
            stages,
            cpuPercentOfOneCore = Math.Round(cpuPercent, 1),
        };

        var path = Path.Combine(AppContext.BaseDirectory, "bench.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(path));
    }

    public static void SelfTest()
    {
        var report = new List<string>();
        var config = new Config();
        config.Normalize();
        var failures = 0;

        foreach (var spec in ControlSchema.Controls)
        {
            try
            {
                switch (spec.Kind)
                {
                    case ControlKind.Range:
                    case ControlKind.Number:
                    {
                        var value = ControlSchema.GetNumber(config, spec.Path);
                        ControlSchema.SetNumber(config, spec.Path, value);
                        var round = ControlSchema.GetNumber(config, spec.Path);
                        if (Math.Abs(round - value) > 1e-4) { failures++; report.Add($"MISMATCH {spec.Path}: {value} -> {round}"); }
                        break;
                    }
                    case ControlKind.Toggle:
                    {
                        var value = ControlSchema.GetBool(config, spec.Path);
                        ControlSchema.SetBool(config, spec.Path, value);
                        var round = ControlSchema.GetBool(config, spec.Path);
                        if (round != value) { failures++; report.Add($"MISMATCH {spec.Path}: {value} -> {round}"); }
                        break;
                    }
                    default:
                    {
                        var value = ControlSchema.GetString(config, spec.Path);
                        ControlSchema.SetString(config, spec.Path, value);
                        var round = ControlSchema.GetString(config, spec.Path);
                        if (round != value) { failures++; report.Add($"MISMATCH {spec.Path}: '{value}' -> '{round}'"); }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                failures++;
                report.Add($"FAIL {spec.Path}: {ex.Message}");
            }
        }

        foreach (var path in new[] { "text", "font", "charset", "cell.w", "colors.hot", "rain.speed", "textMask.gain", "energy.dimCeiling", "fps" })
        {
            report.Add($"{path} -> {ControlSchema.FlagsFor(path)}");
        }

        // The brightness ramp must be monotonic: alpha rising and colour moving
        // toward hot, so rain tails fade instead of stepping between tiers.
        var rampPipeline = new Pipeline(new Config());
        rampPipeline.Layout(1920, 1080, 1);
        var previousAlpha = 0f;
        var previousWhite = -1f;
        for (var i = 0; i <= 20; i++)
        {
            var value = i / 20f;
            var (r, g, b, alpha) = rampPipeline.Renderer.SampleFieldRamp(value);
            var white = (r + g + b) / 3f;
            if (alpha + 1e-3 < previousAlpha) { failures++; report.Add($"RAMP alpha dropped at {value}: {alpha} < {previousAlpha}"); }
            if (white + 1e-3 < previousWhite) { failures++; report.Add($"RAMP colour got bluer at {value}: {white} < {previousWhite}"); }
            previousAlpha = alpha;
            previousWhite = white;
        }
        report.Add($"ramp: alpha {previousAlpha:0.00} at 1.0, colour {previousWhite:0} at 1.0");

        // Word cells must blend toward hot continuously, not switch at a threshold.
        if (Math.Abs(FieldRenderer.WordBlend(0.3f, 0f) - 0.3f) > 1e-4)
        {
            failures++;
            report.Add($"FAIL word blend at rest: {FieldRenderer.WordBlend(0.3f, 0f)}");
        }
        var previousBlend = -1f;
        for (var i = 0; i <= 10; i++)
        {
            var blended = FieldRenderer.WordBlend(0.3f, i / 10f);
            if (blended < previousBlend) { failures++; report.Add($"FAIL word blend not monotonic at {i / 10f}"); }
            previousBlend = blended;
        }
        if (Math.Abs(FieldRenderer.WordBlend(0.3f, 1f) - 1f) > 1e-4)
        {
            failures++;
            report.Add($"FAIL word blend at full pulse: {FieldRenderer.WordBlend(0.3f, 1f)}");
        }
        report.Add($"word blend: 0.30 + 0.0 -> {FieldRenderer.WordBlend(0.3f, 0f):0.00}, + 0.5 -> {FieldRenderer.WordBlend(0.3f, 0.5f):0.00}, + 1.0 -> {FieldRenderer.WordBlend(0.3f, 1f):0.00}");

        // Panel bounds and Normalize clamps must be the same numbers, and the panel
        // must widen instead of snapping an out-of-range value.
        var numericControls = ControlSchema.Controls.Where(c => c.Kind is ControlKind.Range or ControlKind.Number).ToArray();
        foreach (var spec in numericControls)
        {
            if (!Tuning.Ranges.ContainsKey(spec.Path))
            {
                failures++;
                report.Add($"FAIL no tuning range for {spec.Path}");
                continue;
            }

            var range = Tuning.For(spec.Path);
            var probeConfig = new Config();

            ControlSchema.SetNumber(probeConfig, spec.Path, range.Min - 10);
            probeConfig.Normalize();
            var low = ControlSchema.GetNumber(probeConfig, spec.Path);
            if (Math.Abs(low - range.Min) > 0.01) { failures++; report.Add($"FAIL clamp min {spec.Path}: {low} != {range.Min}"); }

            ControlSchema.SetNumber(probeConfig, spec.Path, range.Max + 10);
            probeConfig.Normalize();
            var high = ControlSchema.GetNumber(probeConfig, spec.Path);
            if (Math.Abs(high - range.Max) > 0.01) { failures++; report.Add($"FAIL clamp max {spec.Path}: {high} != {range.Max}"); }

            var extended = Tuning.Effective(spec.Path, range.Max + 5);
            if (extended.Max < range.Max + 5) { failures++; report.Add($"FAIL extend {spec.Path}: {extended.Max}"); }
        }
        report.Add($"ranges: {numericControls.Length} numeric controls match Normalize");

        // RangeValue: JSON shape, ordering, sampling and the scene roll.
        var rangeConfig = new Config();
        rangeConfig.Rain.Speed = RangeValue.Between(20, 30);
        rangeConfig.Rain.Trail = 18;
        rangeConfig.Normalize();

        var rangeJson = rangeConfig.ToJson();
        var rangeBack = JsonSerializer.Deserialize<Config>(rangeJson, Config.JsonOptions)!;
        if (Math.Abs(rangeBack.Rain.Speed.Low - 20) > 1e-3 || Math.Abs(rangeBack.Rain.Speed.High - 30) > 1e-3)
        {
            failures++;
            report.Add($"FAIL range json: [{rangeBack.Rain.Speed.Low}, {rangeBack.Rain.Speed.High}]");
        }
        if (!rangeJson.Contains("\"trail\": 18"))
        {
            failures++;
            report.Add("FAIL fixed value not written as a number");
        }

        var swapped = JsonSerializer.Deserialize<Config>("{\"rain\":{\"speed\":[30,20]}}", Config.JsonOptions)!;
        if (Math.Abs(swapped.Rain.Speed.Low - 20) > 1e-3 || Math.Abs(swapped.Rain.Speed.High - 30) > 1e-3)
        {
            failures++;
            report.Add($"FAIL reversed range: [{swapped.Rain.Speed.Low}, {swapped.Rain.Speed.High}]");
        }

        // Old configs may hold ranges on fields that are scalars again; use the midpoint
        // instead of failing to parse.
        var legacy = JsonSerializer.Deserialize<Config>("{\"energy\":{\"decay\":[0.90,0.96]}}", Config.JsonOptions)!;
        if (Math.Abs(legacy.Energy.Decay - 0.93f) > 1e-3)
        {
            failures++;
            report.Add($"FAIL legacy scalar range: {legacy.Energy.Decay}");
        }

        var sampleOk = true;
        for (var i = 0; i < 1000; i++)
        {
            var sample = rangeConfig.Rain.Speed.Sample();
            if (sample < 20 - 1e-3 || sample > 30 + 1e-3) sampleOk = false;
        }
        if (!sampleOk) { failures++; report.Add("FAIL sample outside range"); }

        var rollConfig = new Config();
        rollConfig.Normalize();
        rollConfig.Randomize.Seconds = 0.05f;
        rollConfig.Rain.PerSecond = RangeValue.Between(1, 80);
        var rollPipeline = new Pipeline(rollConfig);
        rollPipeline.Layout(1920, 1080, 1);

        var firstRate = rollPipeline.RainPerSecond;
        var rolled = false;
        for (var i = 0; i < 30; i++)
        {
            rollPipeline.Step(1f / 30);
            var rate = rollPipeline.RainPerSecond;
            if (Math.Abs(rate - firstRate) > 1e-4) rolled = true;
            if (rate < 1 || rate > 80) { failures++; report.Add($"FAIL rolled rate: {rate}"); break; }
        }
        if (!rolled) { failures++; report.Add("FAIL scene roll never changed values"); }

        report.Add($"range json: speed [{rangeBack.Rain.Speed.Low:0},{rangeBack.Rain.Speed.High:0}], roll changed={rolled}");

        // Startup fade: 0 at t=0, easing to 1 by the configured duration.
        var fadeConfig = new Config();
        fadeConfig.Normalize();
        fadeConfig.FadeIn = 1;
        var fadePipeline = new Pipeline(fadeConfig);
        fadePipeline.Layout(960, 540, 1);
        var fadeStart = fadePipeline.FadeCurrent;
        for (var i = 0; i < 15; i++) fadePipeline.Step(1f / 30);   // 0.5 s
        var fadeHalf = fadePipeline.FadeCurrent;
        for (var i = 0; i < 20; i++) fadePipeline.Step(1f / 30);   // 1.17 s
        var fadeEnd = fadePipeline.FadeCurrent;
        if (fadeStart > 0.01f) { failures++; report.Add($"FAIL fade start: {fadeStart}"); }
        if (Math.Abs(fadeHalf - 0.5f) > 0.05f) { failures++; report.Add($"FAIL fade half: {fadeHalf}"); }
        if (fadeEnd != 1) { failures++; report.Add($"FAIL fade end: {fadeEnd}"); }
        report.Add($"fade: 0s {fadeStart:0.00}, 0.5s {fadeHalf:0.00}, 1.2s {fadeEnd:0.00}");

        // fadeIn 0 means no fade at all: the window is opaque from the first frame.
        var instantConfig = new Config { FadeIn = 0 };
        instantConfig.Normalize();
        var instantPipeline = new Pipeline(instantConfig);
        instantPipeline.Layout(960, 540, 1);
        instantPipeline.Step(1f / 30);
        if (instantConfig.FadeIn != 0 || instantPipeline.FadeCurrent != 1)
        {
            failures++;
            report.Add($"FAIL fade off: config {instantConfig.FadeIn}, fade {instantPipeline.FadeCurrent}");
        }
        report.Add($"fade off: 0s {instantPipeline.FadeCurrent:0.00}");

        if (Math.Abs(Tuning.Snap("textMask.spacing", 2.37f) - 2.5f) > 1e-4)
        {
            failures++;
            report.Add($"FAIL snap spacing: {Tuning.Snap("textMask.spacing", 2.37f)}");
        }
        if (Math.Abs(Tuning.Snap("energy.decay", 0.91234f) - 0.912f) > 1e-4)
        {
            failures++;
            report.Add($"FAIL snap decay: {Tuning.Snap("energy.decay", 0.91234f)}");
        }
        report.Add($"snap: spacing 2.37 -> {Tuning.Snap("textMask.spacing", 2.37f)}, decay 0.91234 -> {Tuning.Snap("energy.decay", 0.91234f)}");

        // Glow on text only: the same drop must light a word cell but leave the field
        // beside it dark, and light both when the flag is off.
        static (float Word, float Field) ProbeGlow(bool textOnly)
        {
            var config = new Config();
            config.Normalize();
            config.Rain.GlowTextOnly = textOnly;
            config.Rain.PerSecond = 0;

            var grid = new CharGrid();
            grid.Resize(21, 23);
            grid.Weight[10 * 21 + 10] = 1;

            var rain = new RainField();
            rain.AddDrop(x: 10.5f, y: 10.5f, size: 2, glow: 4, strength: 1, trail: 1);
            rain.Update(0, grid, config.Rain, config.Rain.PerSecond.Mid);

            return (grid.Energy[10 * 21 + 10], grid.Energy[10 * 21 + 13]);
        }

        var glowOn = ProbeGlow(true);
        var glowOff = ProbeGlow(false);
        report.Add($"glow text-only: word {glowOn.Word:0.00} field {glowOn.Field:0.00} | both: word {glowOff.Word:0.00} field {glowOff.Field:0.00}");
        if (glowOn.Word <= 0) { failures++; report.Add("FAIL glow text-only: word cell not lit"); }
        if (glowOn.Field > 0.01f) { failures++; report.Add("FAIL glow text-only: field cell lit"); }
        if (glowOff.Field <= 0) { failures++; report.Add("FAIL glow both: field cell not lit"); }

        // Switching to a shorter charset must clamp the stale char indices, not crash.
        try
        {
            var charsetPipeline = new Pipeline(new Config());
            charsetPipeline.Layout(1920, 1080, 1);
            for (var i = 0; i < 30; i++) charsetPipeline.Step(1f / 30);
            charsetPipeline.Config.Charset = "binary";
            charsetPipeline.RebuildMasks();
            for (var i = 0; i < 5; i++) charsetPipeline.Step(1f / 30);
            charsetPipeline.Draw();
            report.Add($"charset switch to binary: lit {charsetPipeline.Renderer.Visible}");
        }
        catch (Exception ex)
        {
            failures++;
            report.Add($"FAIL charset switch: {ex.Message}");
        }

        // Exercise each preset through the live pipeline.
        var pipeline = new Pipeline(config) { FadeInEnabled = false };
        pipeline.Layout(1920, 1080, 1);
        foreach (var preset in Presets.All)
        {
            var fresh = new Config();
            preset.Apply(fresh);
            fresh.Normalize();
            config.CopyFrom(fresh);
            pipeline.RebuildMask();
            pipeline.RebuildMasks();
            for (var i = 0; i < 20; i++) pipeline.Step(1f / 30);
            pipeline.Draw();
            report.Add($"preset {preset.Name}: grid {pipeline.Cols}x{pipeline.Rows}, lit {pipeline.Renderer.Visible}");
        }

        report.Insert(0, failures == 0 ? "self-test OK" : $"self-test FAILURES: {failures}");
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }

    public static void Shot(string path, int frames, (int Width, int Height) size)
    {
        var config = Config.Load(App.ConfigPath);
        var pipeline = new Pipeline(config) { FadeInEnabled = false };
        pipeline.Layout(size.Width, size.Height, 1);
        pipeline.Warmup(1.5f);

        for (var i = 0; i < frames; i++)
        {
            pipeline.Step(1f / 30);
            pipeline.Draw();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        pipeline.Renderer.SavePng(path);
        Console.WriteLine($"wrote {path}");
    }

    /// <summary>Times each simulation stage in isolation, for tuning.</summary>
    private static object StageBreakdown(Pipeline pipeline, int iterations)
    {
        double Time(Action action)
        {
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++) action();
            stopwatch.Stop();
            return Math.Round(stopwatch.Elapsed.TotalMilliseconds / iterations, 3);
        }

        var grid = pipeline.Grid;
        var config = pipeline.Config;
        var charset = Config.ResolveCharset(config.Charset);
        var dt = 1f / 30;
        var probeNoise = new NoiseField(1);

        return new
        {
            churn = Time(() => grid.Churn(config.ChurnBuckets, charset.Length)),
            decay = Time(() => grid.Decay(0.94f, 0.95f, 0, 0.005f, 0)),
            noise = Time(() => probeNoise.Fill(grid.Base, pipeline.Cols, pipeline.Rows, new NoiseSettings(config.Noise.Amount, config.Noise.Scale, config.Noise.Speed, config.Noise.Drift, config.Noise.Roughness), 1.2f)),
            rain = Time(() => pipeline.Rain.Update(dt, grid, config.Rain, pipeline.RainPerSecond)),
            ripples = Time(() => pipeline.Ripples.Update(dt, grid, config)),
        };
    }
}
