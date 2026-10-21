using System.Reflection;

namespace Matriosk;

internal enum ControlKind
{
    Range,
    Number,
    Text,
    Color,
    Select,
    Toggle,
}

/// <summary>
/// One tuning row. Numeric bounds are not stored here: they live in
/// <see cref="Tuning.Ranges"/> and are looked up by path, so the panel and the config
/// clamp always agree.
/// </summary>
internal sealed record ControlSpec(
    string Group,
    string Path,
    string Label,
    ControlKind Kind,
    string[]? Options = null,
    string? Hint = null);

internal static class ControlSchema
{
    public static readonly string[] Groups =
    [
        "Text",
        "Field",
        "Startup",
        "Rain",
        "Ripples",
        "Energy",
        "Water",
        "Word mask",
        "Colors",
    ];

    public static readonly string[] CharsetPresets = ["ascii", "binary", "katakana", "punctuation"];

    public static readonly ControlSpec[] Controls =
    [
        // Text
        new("Text", "text", "text", ControlKind.Text),
        new("Text", "textMask.heightCells", "height", ControlKind.Range),
        new("Text", "textMask.fontWeight", "weight", ControlKind.Range),
        new("Text", "textMask.spacing", "spacing", ControlKind.Range),

        // Field
        new("Field", "charset", "charset", ControlKind.Select, Options: CharsetPresets),
        new("Field", "font", "font", ControlKind.Text),
        new("Field", "fps", "fps", ControlKind.Range, Hint: "frame budget"),
        new("Field", "cell.w", "cell w", ControlKind.Range),
        new("Field", "cell.h", "cell h", ControlKind.Range),
        new("Field", "cell.fontScale", "font scale", ControlKind.Range),
        new("Field", "cell.fontWeight", "font weight", ControlKind.Range),
        new("Field", "cell.maxCells", "max cells", ControlKind.Number),
        new("Field", "churnBuckets", "churn rounds", ControlKind.Range),
        new("Field", "randomize.seconds", "re-roll", ControlKind.Range, Hint: "seconds between rain-rate randomisations; 0 = once per load"),

        // Startup
        new("Startup", "fadeIn", "fade in", ControlKind.Range, Hint: "seconds to fade the scene in at startup; 0 = instant"),
        new("Startup", "screensaver", "screensaver", ControlKind.Toggle, Hint: "borderless full screen, hidden cursor, quit on any input; applies at launch or on reload"),

        // Rain
        new("Rain", "rain.perSecond", "drops / s", ControlKind.Range),
        new("Rain", "rain.speed", "fall speed", ControlKind.Range),
        new("Rain", "rain.trail", "trail", ControlKind.Range),
        new("Rain", "rain.trailDecay", "trail decay", ControlKind.Range),
        new("Rain", "rain.size", "drop size", ControlKind.Range),
        new("Rain", "rain.glow", "glow radius", ControlKind.Range),
        new("Rain", "rain.glowTextOnly", "glow on text only", ControlKind.Toggle, Hint: "the radial glow lights word cells only; the head and tail stay visible"),
        new("Rain", "rain.strength", "strength", ControlKind.Range),
        new("Rain", "rain.max", "max drops", ControlKind.Number),

        // Ripples
        new("Ripples", "ripple.speed", "speed", ControlKind.Range),
        new("Ripples", "ripple.life", "life", ControlKind.Range),
        new("Ripples", "ripple.width", "band width", ControlKind.Range),
        new("Ripples", "ripple.strength", "strength", ControlKind.Range),
        new("Ripples", "ripple.noise", "noise", ControlKind.Range, Hint: "0 = perfect circle"),
        new("Ripples", "ripple.scale", "noise scale", ControlKind.Range),
        new("Ripples", "ripple.field", "field gain", ControlKind.Range),
        new("Ripples", "ripple.max", "max ripples", ControlKind.Number),

        // Energy
        new("Energy", "energy.decay", "decay", ControlKind.Range),
        new("Energy", "energy.dimCeiling", "dim ceiling", ControlKind.Range),
        new("Energy", "energy.brightThreshold", "bright at", ControlKind.Range),
        new("Energy", "energy.minVisible", "min visible", ControlKind.Range),

        // Water
        new("Water", "noise.amount", "amount", ControlKind.Range),
        new("Water", "noise.scale", "scale", ControlKind.Range),
        new("Water", "noise.speed", "speed", ControlKind.Range),
        new("Water", "noise.drift", "drift", ControlKind.Range),
        new("Water", "noise.roughness", "roughness", ControlKind.Range),

        // Word mask
        new("Word mask", "textMask.floor", "floor", ControlKind.Range),
        new("Word mask", "textMask.gain", "bloom", ControlKind.Range),
        new("Word mask", "textMask.noiseGain", "water gain", ControlKind.Range),
        new("Word mask", "textMask.decay", "bloom decay", ControlKind.Range),
        new("Word mask", "textMask.dilate", "dilate", ControlKind.Number),
        new("Word mask", "textMask.threshold", "threshold", ControlKind.Range),

        // Colors
        new("Colors", "colors.background", "background", ControlKind.Color),
        new("Colors", "colors.dim", "dim", ControlKind.Color),
        new("Colors", "colors.hot", "hot", ControlKind.Color),
        new("Colors", "colors.glow", "glow size", ControlKind.Range),
    ];

    public static object ResolveTarget(Config config, string path, out PropertyInfo property)
    {
        var parts = path.Split('.');
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        object target = config;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var step = target.GetType().GetProperty(parts[i], flags);
            if (step?.GetValue(target) is not { } next) throw new InvalidOperationException($"bad path {path}");
            target = next;
        }

        property = target.GetType().GetProperty(parts[^1], flags)
                   ?? throw new InvalidOperationException($"bad path {path}");
        return target;
    }

    public static double GetNumber(Config config, string path)
    {
        var target = ResolveTarget(config, path, out var property);
        var value = property.GetValue(target);
        return value is RangeValue range ? range.Low : Convert.ToDouble(value);
    }

    public static void SetNumber(Config config, string path, double value)
    {
        var target = ResolveTarget(config, path, out var property);
        if (property.PropertyType == typeof(RangeValue))
        {
            property.SetValue(target, RangeValue.Fixed((float)value));
            return;
        }

        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        property.SetValue(target, Convert.ChangeType(value, type));
    }

    /// <summary>Reads a value that may be a fixed scalar or a [low, high] range.</summary>
    public static (double Low, double High) GetRange(Config config, string path)
    {
        var target = ResolveTarget(config, path, out var property);
        var value = property.GetValue(target);
        if (value is RangeValue range) return (range.Low, range.High);
        var number = Convert.ToDouble(value);
        return (number, number);
    }

    /// <summary>True when the config key can hold a random range (a RangeValue).</summary>
    public static bool IsRanged(Config config, string path)
    {
        ResolveTarget(config, path, out var property);
        return property.PropertyType == typeof(RangeValue);
    }

    public static void SetRange(Config config, string path, double low, double high)
    {
        var target = ResolveTarget(config, path, out var property);
        if (property.PropertyType == typeof(RangeValue))
        {
            property.SetValue(target, RangeValue.Between((float)low, (float)high));
            return;
        }

        // Scalars cannot hold a range: collapse to the low end.
        SetNumber(config, path, low);
    }

    public static string GetString(Config config, string path)
    {
        var target = ResolveTarget(config, path, out var property);
        return property.GetValue(target)?.ToString() ?? string.Empty;
    }

    public static void SetString(Config config, string path, string value)
    {
        var target = ResolveTarget(config, path, out var property);
        property.SetValue(target, value);
    }

    public static bool GetBool(Config config, string path)
    {
        var target = ResolveTarget(config, path, out var property);
        return Convert.ToBoolean(property.GetValue(target));
    }

    public static void SetBool(Config config, string path, bool value)
    {
        var target = ResolveTarget(config, path, out var property);
        property.SetValue(target, value);
    }

    public static string Format(double value, double step)
    {
        var decimals = step >= 1 ? 0 : Math.Min(4, (int)Math.Ceiling(-Math.Log10(step)));
        return value.ToString($"F{decimals}");
    }

    /// <summary>What has to be rebuilt after a given key changes.</summary>
    public static ChangeFlags FlagsFor(string path) => path switch
    {
        "cell.w" or "cell.h" or "cell.maxCells" => ChangeFlags.Layout,
        "text" => ChangeFlags.Mask,
        "font" => ChangeFlags.Glyphs | ChangeFlags.Mask,
        "charset" or "cell.fontScale" or "cell.fontWeight" => ChangeFlags.Glyphs,
        "colors.glow" => ChangeFlags.Glyphs | ChangeFlags.Palettes,
        "colors.background" or "colors.dim" or "colors.hot" => ChangeFlags.Palettes,
        "screensaver" => ChangeFlags.None,
        "energy.dimCeiling" => ChangeFlags.Palettes,
        _ when path.StartsWith("textMask.", StringComparison.Ordinal) => ChangeFlags.Mask,
        _ => ChangeFlags.None,
    };
}
