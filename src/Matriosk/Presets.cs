namespace Matriosk;

internal sealed record Preset(string Name, string Note, Action<Config> Apply);

/// <summary>Preset examples for the tuning panel, applied on top of the defaults.</summary>
internal static class Presets
{
    public static readonly Preset[] All =
    [
        new("Ice", "cool field, white word", _ => { }),

        new("Mono", "gray field, white word", c =>
        {
            c.Colors.Dim = "#6b6b6b";
            c.Colors.Hot = "#ffffff";
        }),

        new("Phosphor", "green terminal", c =>
        {
            c.Colors.Background = "#03080a";
            c.Colors.Dim = "#1f8f4a";
            c.Colors.Hot = "#b8ffd0";
            c.Colors.Glow = 1.3f;
        }),

        new("Amber", "warm ember, low blue", c =>
        {
            c.Colors.Background = "#070502";
            c.Colors.Dim = "#8a6a3c";
            c.Colors.Hot = "#fff4e0";
            c.Colors.Glow = 1.2f;
        }),

        new("Dense rain", "small cells, many fast drops", c =>
        {
            c.Cell.W = 5;
            c.Cell.H = 9;
            c.ChurnBuckets = 40;
            c.Rain.PerSecond = 18;
            c.Rain.Speed = 40;
            c.Rain.Trail = 22;
            c.Rain.TrailDecay = 1.4f;
            c.Rain.Size = 0.9f;
            c.Rain.Glow = 3;
            c.Rain.Strength = 0.85f;
            c.Rain.Max = 80;
            c.Ripple.Speed = 20;
            c.Ripple.Life = 0.8f;
            c.Ripple.Width = 1.6f;
            c.Ripple.Strength = 0.8f;
            c.Ripple.Noise = 0.4f;
            c.Ripple.Scale = 0.18f;
            c.Energy.Decay = 0.93f;
            c.Energy.DimCeiling = 0.24f;
            c.TextMask.HeightCells = 46;
            c.TextMask.Spacing = 1;
            c.TextMask.Floor = 0;
        }),

        new("Drizzle", "sparse slow drops, long glow", c =>
        {
            c.ChurnBuckets = 30;
            c.Rain.PerSecond = 2;
            c.Rain.Speed = 14;
            c.Rain.Trail = 30;
            c.Rain.TrailDecay = 0.8f;
            c.Rain.Size = 2.2f;
            c.Rain.Glow = 7;
            c.Rain.Strength = 0.8f;
            c.Rain.Max = 20;
            c.Ripple.Speed = 8;
            c.Ripple.Life = 1.8f;
            c.Ripple.Width = 3.5f;
            c.Ripple.Strength = 1;
            c.Ripple.Noise = 0.5f;
            c.Ripple.Scale = 0.1f;
            c.Ripple.Max = 8;
            c.Energy.Decay = 0.965f;
            c.Energy.DimCeiling = 0.28f;
            c.Noise.Amount = 0.35f;
            c.Noise.Scale = 0.045f;
            c.Noise.Speed = 0.18f;
            c.Noise.Drift = 1.2f;
            c.Noise.Roughness = 0.35f;
            c.Colors.Glow = 0.9f;
        }),
    ];
}
