namespace Matriosk;

/// <summary>
/// Ripples raised when a raindrop first touches the configured word. They travel
/// outward through the mask (bright) and the field (faint), with a Perlin-distorted
/// radius so the front is organic rather than a perfect circle.
/// </summary>
public struct Ripple
{
    public float X;
    public float Y;
    public float Age;
    public float Seed;

    // Per-ripple tuning, sampled at spawn so a range in the config varies each ripple.
    public float Speed;
    public float Life;
    public float Width;
    public float Strength;
    public float Noise;
    public float Scale;
    public float Field;
}

public sealed class RippleField
{
    private readonly NoiseField noise = new();
    private Ripple[] ripples = new Ripple[16];

    public int Count { get; private set; }

    public void Clear() => Count = 0;

    public void Spawn(float x, float y, RippleConfig tuning)
    {
        var limit = Math.Max(1, tuning.Max);
        if (Count >= limit)
        {
            Array.Copy(ripples, 1, ripples, 0, Count - 1);
            Count--;
        }
        if (Count == ripples.Length) Array.Resize(ref ripples, ripples.Length * 2);
        ripples[Count++] = new Ripple
        {
            X = x,
            Y = y,
            Age = 0,
            Seed = Random.Shared.NextSingle() * 100,
            Speed = tuning.Speed.Sample(),
            Life = tuning.Life.Sample(),
            Width = tuning.Width.Sample(),
            Strength = tuning.Strength.Sample(),
            Noise = tuning.Noise.Sample(),
            Scale = tuning.Scale.Sample(),
            Field = tuning.Field.Sample(),
        };
    }

    public void Update(float dt, CharGrid grid, Config cfg)
    {
        if (Count == 0) return;

        var cols = grid.Cols;
        var energy = grid.Energy;
        var weight = grid.Weight;

        for (var i = Count - 1; i >= 0; i--)
        {
            ref var ripple = ref ripples[i];
            ripple.Age += dt;
            if (ripple.Age >= ripple.Life)
            {
                ripples[i] = ripples[Count - 1];
                Count--;
                continue;
            }

            var width = MathF.Max(0.5f, ripple.Width);
            var radius = ripple.Age * ripple.Speed;
            var wobble = MathF.Max(0, ripple.Noise);
            var reach = radius * (1 + wobble) + width;
            var fade = 1 - ripple.Age / ripple.Life;
            var power = ripple.Strength * fade * fade;

            var xStart = Math.Max(0, (int)MathF.Floor(ripple.X - reach));
            var xEnd = Math.Min(cols - 1, (int)MathF.Floor(ripple.X + reach));
            var yStart = Math.Max(0, (int)MathF.Floor(ripple.Y - reach));
            var yEnd = Math.Min(grid.Rows - 1, (int)MathF.Floor(ripple.Y + reach));

            for (var y = yStart; y <= yEnd; y++)
            {
                var dy = y + 0.5f - ripple.Y;
                var rowOffset = y * cols;
                for (var x = xStart; x <= xEnd; x++)
                {
                    var index = rowOffset + x;
                    var dx = x + 0.5f - ripple.X;
                    var dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist > reach) continue;

                    var offset = noise.Noise(x * ripple.Scale, y * ripple.Scale, ripple.Seed);
                    var edge = radius * (1 + wobble * offset);
                    var band = 1 - MathF.Abs(dist - edge) / width;
                    if (band <= 0) continue;

                    var gain = weight[index] > 0.05f ? 1f : ripple.Field;
                    if (gain <= 0) continue;

                    var target = power * gain * (band * band * (3 - 2 * band));
                    if (target > energy[index]) energy[index] = target;
                }
            }
        }
    }
}
