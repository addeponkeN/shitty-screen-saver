namespace Matriosk;

/// <summary>The legal range and step for one numeric config key.</summary>
internal readonly record struct TuningRange(float Min, float Max, float Step);

/// <summary>
/// Single source of truth for numeric bounds. Both the config clamp and the tuning
/// panel read from here, so the sliders can never drift from what Normalize accepts.
/// </summary>
internal static class Tuning
{
    public static readonly IReadOnlyDictionary<string, TuningRange> Ranges = new Dictionary<string, TuningRange>
    {
        // Field
        ["fps"] = new(10, 120, 1),
        ["fadeIn"] = new(0, 60, 0.5f),
        ["cell.w"] = new(3, 60, 1),
        ["cell.h"] = new(4, 80, 1),
        ["cell.fontScale"] = new(0.3f, 2, 0.05f),
        ["cell.fontWeight"] = new(100, 900, 50),
        ["cell.maxCells"] = new(1000, 400000, 1000),
        ["churnBuckets"] = new(1, 240, 1),

        // Rain
        ["rain.perSecond"] = new(0, 80, 0.5f),
        ["rain.speed"] = new(1, 300, 1),
        ["rain.trail"] = new(1, 100, 1),
        ["rain.trailDecay"] = new(0.2f, 6, 0.1f),
        ["rain.size"] = new(0.4f, 20, 0.1f),
        ["rain.glow"] = new(0.4f, 30, 0.1f),
        ["rain.strength"] = new(0, 3, 0.05f),
        ["rain.max"] = new(1, 400, 1),

        // Ripples
        ["ripple.speed"] = new(1, 120, 0.5f),
        ["ripple.life"] = new(0.1f, 12, 0.05f),
        ["ripple.width"] = new(0.5f, 20, 0.1f),
        ["ripple.strength"] = new(0, 3, 0.05f),
        ["ripple.noise"] = new(0, 1.5f, 0.01f),
        ["ripple.scale"] = new(0.01f, 1, 0.01f),
        ["ripple.field"] = new(0, 1, 0.01f),
        ["ripple.max"] = new(1, 100, 1),

        // Energy
        ["energy.decay"] = new(0.5f, 0.9999f, 0.001f),
        ["energy.dimCeiling"] = new(0.01f, 1, 0.01f),
        ["energy.brightThreshold"] = new(0.1f, 1, 0.01f),
        ["energy.minVisible"] = new(0.001f, 0.5f, 0.001f),

        // Water
        ["noise.amount"] = new(0, 2, 0.01f),
        ["noise.scale"] = new(0.001f, 1, 0.001f),
        ["noise.speed"] = new(0, 8, 0.01f),
        ["noise.drift"] = new(-50, 50, 0.5f),
        ["noise.roughness"] = new(0, 1, 0.01f),

        // Word mask
        ["textMask.heightCells"] = new(3, 400, 1),
        ["textMask.fontWeight"] = new(100, 900, 50),
        ["textMask.spacing"] = new(0, 20, 0.5f),
        ["textMask.floor"] = new(0, 1, 0.01f),
        ["textMask.gain"] = new(0, 20, 0.1f),
        ["textMask.noiseGain"] = new(0, 20, 0.1f),
        ["textMask.decay"] = new(0.5f, 0.9999f, 0.001f),
        ["textMask.dilate"] = new(0, 6, 1),
        ["textMask.threshold"] = new(0, 1, 0.01f),

        // Colors
        ["colors.glow"] = new(0, 6, 0.05f),

        // Randomisation cadence
        ["randomize.seconds"] = new(0, 600, 0.5f),
    };

    public static TuningRange For(string path) =>
        Ranges.TryGetValue(path, out var range)
            ? range
            : throw new InvalidOperationException($"no tuning range for '{path}'");

    /// <summary>
    /// The range a control should actually use: if the config holds a value outside the
    /// declared bounds, the control grows to include it instead of snapping the value.
    /// </summary>
    public static TuningRange Effective(string path, float value)
    {
        var range = For(path);
        return range with
        {
            Min = MathF.Min(range.Min, value),
            Max = MathF.Max(range.Max, value),
        };
    }

    public static float Clamp(string path, float value)
    {
        var range = For(path);
        return float.IsFinite(value) ? Math.Clamp(value, range.Min, range.Max) : range.Min;
    }

    public static int Clamp(string path, int value)
    {
        var range = For(path);
        return Math.Clamp(value, (int)MathF.Floor(range.Min), (int)MathF.Ceiling(range.Max));
    }

    /// <summary>Rounds to the control's step grid, relative to its minimum.</summary>
    public static float Snap(string path, float value)
    {
        var range = For(path);
        if (range.Step <= 0) return value;
        var steps = MathF.Round((value - range.Min) / range.Step);
        return range.Min + steps * range.Step;
    }
}
