using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Matriosk;

public sealed class CellConfig
{
    public float W { get; set; } = 7;
    public float H { get; set; } = 12;
    public float FontScale { get; set; } = 1;
    public float FontWeight { get; set; } = 500;
    public int MaxCells { get; set; } = 30000;
}

/// <summary>
/// Rain tuning. Every numeric effect value accepts either a number (fixed) or a
/// two-element array (sampled per drop).
/// </summary>
public sealed class RainConfig
{
    public RangeValue PerSecond { get; set; } = 6;
    public RangeValue Speed { get; set; } = 26;
    public RangeValue Trail { get; set; } = 18;
    public RangeValue TrailDecay { get; set; } = 1.2f;
    public RangeValue Size { get; set; } = 2;
    public RangeValue Glow { get; set; } = 4;
    public RangeValue Strength { get; set; } = 0.9f;
    public int Max { get; set; } = 40;

    /// <summary>Restrict the radial glow to word cells; the head and tail still show.</summary>
    public bool GlowTextOnly { get; set; } = true;
}

public sealed class RippleConfig
{
    public RangeValue Speed { get; set; } = 12;
    public RangeValue Life { get; set; } = 1.4f;
    public RangeValue Width { get; set; } = 3;
    public RangeValue Strength { get; set; } = 1;
    public RangeValue Noise { get; set; } = 0.4f;
    public RangeValue Scale { get; set; } = 0.14f;
    public RangeValue Field { get; set; } = 0.35f;
    public int Max { get; set; } = 10;
}

public sealed class EnergyConfig
{
    public float Decay { get; set; } = 0.94f;
    public float DimCeiling { get; set; } = 0.3f;
    public float BrightThreshold { get; set; } = 0.45f;
    public float MinVisible { get; set; } = 0.15f;
}

public sealed class NoiseConfig
{
    public float Amount { get; set; } = 0.35f;
    public float Scale { get; set; } = 0.05f;
    public float Speed { get; set; } = 0.25f;
    public float Drift { get; set; } = 2.5f;
    public float Roughness { get; set; } = 0.45f;
}

public sealed class TextMaskConfig
{
    public float HeightCells { get; set; } = 32;
    public float FontWeight { get; set; } = 400;
    public float Spacing { get; set; } = 2;
    public float Floor { get; set; }
    public float Gain { get; set; }
    public float NoiseGain { get; set; } = 0.5f;
    public float Decay { get; set; } = 0.99f;
    public int Dilate { get; set; }
    public float Threshold { get; set; } = 0.1f;
}

public sealed class ColorConfig
{
    public string Background { get; set; } = "#05070a";
    public string Dim { get; set; } = "#7aa2b8";
    public string Hot { get; set; } = "#ffffff";
    public float Glow { get; set; } = 1.1f;
}

public sealed class RandomizeConfig
{
    /// <summary>Seconds between scene randomisations. 0 rolls once per load.</summary>
    public float Seconds { get; set; } = 8;
}

public sealed class Config
{
    public string Text { get; set; } = "HELLO";
    public string Charset { get; set; } = "ascii";
    public string Font { get; set; } = "Consolas";
    public int Fps { get; set; } = 30;

    /// <summary>Seconds to fade the whole scene in at startup. 0 disables it.</summary>
    public float FadeIn { get; set; } = 5;

    /// <summary>Borderless fullscreen, hidden cursor, exit on any input.</summary>
    public bool Screensaver { get; set; } = false;

    public CellConfig Cell { get; set; } = new();
    public int ChurnBuckets { get; set; } = 20;
    public RainConfig Rain { get; set; } = new();
    public RippleConfig Ripple { get; set; } = new();
    public EnergyConfig Energy { get; set; } = new();
    public NoiseConfig Noise { get; set; } = new();
    public TextMaskConfig TextMask { get; set; } = new();
    public ColorConfig Colors { get; set; } = new();
    public RandomizeConfig Randomize { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new RangeValueJsonConverter(), new LenientFloatConverter() },
    };

    private static readonly IReadOnlyDictionary<string, string> Presets = new Dictionary<string, string>
    {
        ["ascii"] = new string(Enumerable.Range(33, 94).Select(i => (char)i).ToArray()),
        ["binary"] = "01",
        ["katakana"] = new string(Enumerable.Range(0xFF66, 0xFF9D - 0xFF66 + 1).Select(i => (char)i).ToArray()),
        ["punctuation"] = ".:;+*#%@",
    };

    public static string ResolveCharset(string value)
    {
        if (Presets.TryGetValue(value, out var preset)) return preset;
        return string.IsNullOrEmpty(value) ? Presets["ascii"] : value;
    }

    public static Config Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Config>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[matriosk] config load failed: {ex.Message}");
        }

        var fallback = new Config();
        fallback.Normalize();
        return fallback;
    }

    public void CopyFrom(Config other)
    {
        Text = other.Text;
        Charset = other.Charset;
        Font = other.Font;
        Fps = other.Fps;
        FadeIn = other.FadeIn;
        Screensaver = other.Screensaver;
        Cell = other.Cell;
        ChurnBuckets = other.ChurnBuckets;
        Rain = other.Rain;
        Ripple = other.Ripple;
        Energy = other.Energy;
        Noise = other.Noise;
        TextMask = other.TextMask;
        Colors = other.Colors;
        Randomize = other.Randomize;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public void Normalize()
    {
        Text = string.IsNullOrEmpty(Text) ? "HELLO" : Text;
        Charset = string.IsNullOrEmpty(Charset) ? "ascii" : Charset;
        Font = string.IsNullOrWhiteSpace(Font) ? "Consolas" : Font;

        // Bounds come from Tuning.Ranges, shared with the tuning panel so the sliders
        // and the accepted config values can't drift apart.
        Fps = Tuning.Clamp("fps", Fps);
        FadeIn = Tuning.Clamp("fadeIn", FadeIn);

        Cell.W = Tuning.Clamp("cell.w", Cell.W);
        Cell.H = Tuning.Clamp("cell.h", Cell.H);
        Cell.FontScale = Tuning.Clamp("cell.fontScale", Cell.FontScale);
        Cell.FontWeight = Tuning.Clamp("cell.fontWeight", Cell.FontWeight);
        Cell.MaxCells = Tuning.Clamp("cell.maxCells", Cell.MaxCells);

        ChurnBuckets = Tuning.Clamp("churnBuckets", ChurnBuckets);

        Rain.PerSecond = ClampRange("rain.perSecond", Rain.PerSecond);
        Rain.Speed = ClampRange("rain.speed", Rain.Speed);
        Rain.Trail = ClampRange("rain.trail", Rain.Trail);
        Rain.TrailDecay = ClampRange("rain.trailDecay", Rain.TrailDecay);
        Rain.Size = ClampRange("rain.size", Rain.Size);
        Rain.Glow = ClampRange("rain.glow", Rain.Glow);
        Rain.Strength = ClampRange("rain.strength", Rain.Strength);
        Rain.Max = Tuning.Clamp("rain.max", Rain.Max);

        Ripple.Speed = ClampRange("ripple.speed", Ripple.Speed);
        Ripple.Life = ClampRange("ripple.life", Ripple.Life);
        Ripple.Width = ClampRange("ripple.width", Ripple.Width);
        Ripple.Strength = ClampRange("ripple.strength", Ripple.Strength);
        Ripple.Noise = ClampRange("ripple.noise", Ripple.Noise);
        Ripple.Scale = ClampRange("ripple.scale", Ripple.Scale);
        Ripple.Field = ClampRange("ripple.field", Ripple.Field);
        Ripple.Max = Tuning.Clamp("ripple.max", Ripple.Max);

        Energy.Decay = Tuning.Clamp("energy.decay", Energy.Decay);
        Energy.DimCeiling = Tuning.Clamp("energy.dimCeiling", Energy.DimCeiling);
        Energy.BrightThreshold = Tuning.Clamp("energy.brightThreshold", Energy.BrightThreshold);
        Energy.MinVisible = Tuning.Clamp("energy.minVisible", Energy.MinVisible);

        Noise.Amount = Tuning.Clamp("noise.amount", Noise.Amount);
        Noise.Scale = Tuning.Clamp("noise.scale", Noise.Scale);
        Noise.Speed = Tuning.Clamp("noise.speed", Noise.Speed);
        Noise.Drift = Tuning.Clamp("noise.drift", Noise.Drift);
        Noise.Roughness = Tuning.Clamp("noise.roughness", Noise.Roughness);

        TextMask.HeightCells = Tuning.Clamp("textMask.heightCells", TextMask.HeightCells);
        TextMask.FontWeight = Tuning.Clamp("textMask.fontWeight", TextMask.FontWeight);
        TextMask.Spacing = Tuning.Clamp("textMask.spacing", TextMask.Spacing);
        TextMask.Floor = Tuning.Clamp("textMask.floor", TextMask.Floor);
        TextMask.Gain = Tuning.Clamp("textMask.gain", TextMask.Gain);
        TextMask.NoiseGain = Tuning.Clamp("textMask.noiseGain", TextMask.NoiseGain);
        TextMask.Decay = Tuning.Clamp("textMask.decay", TextMask.Decay);
        TextMask.Dilate = Tuning.Clamp("textMask.dilate", TextMask.Dilate);
        TextMask.Threshold = Tuning.Clamp("textMask.threshold", TextMask.Threshold);

        Colors.Glow = Tuning.Clamp("colors.glow", Colors.Glow);

        Randomize.Seconds = Tuning.Clamp("randomize.seconds", Randomize.Seconds);
    }

    private static RangeValue ClampRange(string path, RangeValue value) =>
        new(Tuning.Clamp(path, value.Low), Tuning.Clamp(path, value.High));

    /// <summary>Parses "#rrggbb" (or "#rgb"), falling back to white.</summary>
    public static (byte R, byte G, byte B) ParseColor(string hex, (byte, byte, byte) fallback = default)
    {
        var value = (hex ?? string.Empty).Trim().TrimStart('#');
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(c => new string(c, 2)));
        }
        if (value.Length == 6 && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return ((byte)((parsed >> 16) & 0xFF), (byte)((parsed >> 8) & 0xFF), (byte)(parsed & 0xFF));
        }
        return fallback == default ? ((byte)255, (byte)255, (byte)255) : fallback;
    }
}
